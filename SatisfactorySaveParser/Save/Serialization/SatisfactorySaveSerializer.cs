﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Ionic.Zlib;

using NLog;

using SatisfactorySaveParser.Game.Enums;
using SatisfactorySaveParser.Save.Properties;

namespace SatisfactorySaveParser.Save.Serialization
{
    /// <summary>
    ///     A serializer that supports versions 4 and 5 of the satisfactory save format
    /// </summary>
    public class SatisfactorySaveSerializer : ISaveSerializer
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();
        private static readonly HashSet<string> missingProperties = new HashSet<string>();

        public event EventHandler<StageChangedEventArgs> SerializationStageChanged;
        public event EventHandler<StageProgressedEventArgs> SerializationStageProgressed;
        public event EventHandler<StageChangedEventArgs> DeserializationStageChanged;
        public event EventHandler<StageProgressedEventArgs> DeserializationStageProgressed;

        private int currentDeserializationStage = 0;

        private void IncrementDeserializationStage(SerializerStage stage)
        {
            DeserializationStageChanged?.Invoke(this, new StageChangedEventArgs()
            {
                Stage = stage,
                Current = currentDeserializationStage++,
                Total = 7
            });
        }

        public FGSaveSession Deserialize(Stream stream)
        {
            currentDeserializationStage = 0;
            IncrementDeserializationStage(SerializerStage.FileOpen);

            var sw = Stopwatch.StartNew();
            using var reader = new BinaryReader(stream);

            IncrementDeserializationStage(SerializerStage.ParseHeader);
            var save = new FGSaveSession
            {
                Header = DeserializeHeader(reader)
            };

            log.Info($"Save is {(save.Header.IsCompressed ? "compressed" : "not compressed")}");
            // Go through the stage either way to make the UI consistent
            IncrementDeserializationStage(SerializerStage.Decompressing);
            if (!save.Header.IsCompressed)
            {
                DeserializeSaveData(save, reader);
            }
            else
            {
                using var uncompressedBuffer = new MemoryStream();
                var uncompressedSize = 0L;

                while (stream.Position < stream.Length)
                {
                    var chunkHeader = reader.ReadCompressedChunkHeader();
                    Trace.Assert(chunkHeader.PackageTag == FCompressedChunkHeader.Magic);

                    var chunkInfo = reader.ReadCompressedChunkInfo();
                    Trace.Assert(chunkHeader.UncompressedSize == chunkInfo.UncompressedSize);

                    var startPosition = stream.Position;
                    using (var zStream = new ZlibStream(stream, CompressionMode.Decompress, true))
                    {
                        zStream.CopyTo(uncompressedBuffer);
                    }

                    // ZlibStream appears to read more bytes than it uses (because of buffering probably) so we need to manually fix the input stream position
                    stream.Position = startPosition + chunkInfo.CompressedSize;

                    uncompressedSize += chunkInfo.UncompressedSize;
                }

                uncompressedBuffer.Position = 0;
                using (var uncompressedReader = new BinaryReader(uncompressedBuffer))
                {
                    var dataLength = uncompressedReader.ReadInt32();
                    Trace.Assert(uncompressedSize == dataLength + 4);

                    DeserializeSaveData(save, uncompressedReader);
                }
            }

            sw.Stop();
            IncrementDeserializationStage(SerializerStage.Done);
            log.Info($"Parsing save took {sw.ElapsedMilliseconds / 1000f}s");

            return save;
        }

        private void DeserializeSaveData(FGSaveSession save, BinaryReader reader)
        {
            IncrementDeserializationStage(SerializerStage.ReadObjects);

            // Does not need to be a public property because it's equal to Entries.Count
            var totalSaveObjects = reader.ReadUInt32();
            log.Info($"Save contains {totalSaveObjects} object headers");

            for (int i = 0; i < totalSaveObjects; i++)
            {
                save.Objects.Add(DeserializeObjectHeader(reader));
            }

            IncrementDeserializationStage(SerializerStage.ReadObjectData);

            var totalSaveObjectData = reader.ReadInt32();
            log.Info($"Save contains {totalSaveObjectData} object data");

            Trace.Assert(save.Objects.Count == totalSaveObjects);
            Trace.Assert(save.Objects.Count == totalSaveObjectData);

            for (int i = 0; i < save.Objects.Count; i++)
            {
                DeserializeObjectData(save.Objects[i], reader);
            }

            IncrementDeserializationStage(SerializerStage.ReadDestroyedObjects);

            save.DestroyedActors.AddRange(DeserializeDestroyedActors(reader));

            log.Debug($"Read {reader.BaseStream.Position} of total {reader.BaseStream.Length} bytes");
            Trace.Assert(reader.BaseStream.Position == reader.BaseStream.Length);
        }

        public void Serialize(FGSaveSession save, Stream stream)
        {
            using var writer = new BinaryWriter(stream);

            SerializeHeader(save.Header, writer);

            writer.Write(save.Objects.Count);

            var actors = save.Objects.Where(e => e is SaveActor).ToArray();
            foreach (var actor in actors)
                SerializeObjectHeader(actor, writer);

            var components = save.Objects.Where(e => e is SaveComponent).ToArray();
            foreach (var component in components)
                SerializeObjectHeader(component, writer);


            writer.Write(actors.Length + components.Length);

            foreach (var actor in actors)
                SerializeObjectData(actor, writer);

            foreach (var component in components)
                SerializeObjectData(component, writer);


            SerializeDestroyedActors(save.DestroyedActors, writer);
        }

        public static FSaveHeader DeserializeHeader(BinaryReader reader)
        {
            var headerVersion = (FSaveHeaderVersion)reader.ReadInt32();
            var saveVersion = (FSaveCustomVersion)reader.ReadInt32();

            if (headerVersion > FSaveHeaderVersion.LatestVersion)
                throw new UnsupportedHeaderVersionException(headerVersion);

            if (saveVersion > FSaveCustomVersion.LatestVersion)
                throw new UnsupportedSaveVersionException(saveVersion);

            var header = new FSaveHeader
            {
                HeaderVersion = headerVersion,
                SaveVersion = saveVersion,
                BuildVersion = reader.ReadInt32(),

                MapName = reader.ReadLengthPrefixedString(),
                MapOptions = reader.ReadLengthPrefixedString(),
                SessionName = reader.ReadLengthPrefixedString(),

                PlayDuration = reader.ReadInt32(),
                SaveDateTime = reader.ReadInt64()
            };

            var logStr = $"Read save header: HeaderVersion={header.HeaderVersion}, SaveVersion={header.SaveVersion}, BuildVersion={header.BuildVersion}, MapName={header.MapName}, MapOpts={header.MapOptions}, SessionName={header.SessionName}, PlayTime={header.PlayDuration}, SaveTime={header.SaveDateTime}";

            if (header.SupportsSessionVisibility)
            {
                header.SessionVisibility = (ESessionVisibility)reader.ReadByte();
                logStr += $", Visibility={header.SessionVisibility}";
            }

            log.Debug(logStr);

            return header;
        }

        public static void SerializeHeader(FSaveHeader header, BinaryWriter writer)
        {
            writer.Write((int)header.HeaderVersion);
            writer.Write((int)header.SaveVersion);
            writer.Write(header.BuildVersion);

            writer.WriteLengthPrefixedString(header.MapName);
            writer.WriteLengthPrefixedString(header.MapOptions);
            writer.WriteLengthPrefixedString(header.SessionName);

            writer.Write(header.PlayDuration);
            writer.Write(header.SaveDateTime);

            if (header.SupportsSessionVisibility)
                writer.Write((byte)header.SessionVisibility);
        }

        public static SaveObject DeserializeObjectHeader(BinaryReader reader)
        {
            var kind = (SaveObjectKind)reader.ReadInt32();
            var className = string.Intern(reader.ReadLengthPrefixedString());

            var saveObject = SaveObjectFactory.CreateFromClass(kind, className);
            saveObject.Instance = reader.ReadObjectReference();

            switch (saveObject)
            {
                case SaveActor actor:
                    actor.NeedTransform = reader.ReadInt32() == 1;
                    actor.Rotation = reader.ReadVector4();
                    actor.Position = reader.ReadVector3();
                    actor.Scale = reader.ReadVector3();

                    if (actor.Scale.IsSuspicious())
                        log.Warn($"Actor {actor} has suspicious scale {actor.Scale}");

                    actor.WasPlacedInLevel = reader.ReadInt32() == 1;
                    break;

                case SaveComponent component:
                    component.ParentEntityName = reader.ReadLengthPrefixedString();
                    break;

                default:
                    throw new NotImplementedException($"Unknown SaveObject kind {kind}");
            }

            return saveObject;
        }

        public static void SerializeObjectHeader(SaveObject saveObject, BinaryWriter writer)
        {
            writer.Write((int)saveObject.ObjectKind);
            writer.WriteLengthPrefixedString(saveObject.TypePath);
            writer.Write(saveObject.Instance);

            switch (saveObject)
            {
                case SaveActor actor:
                    writer.Write(actor.NeedTransform ? 1 : 0);
                    writer.Write(actor.Rotation);
                    writer.Write(actor.Position);
                    writer.Write(actor.Scale);
                    writer.Write(actor.WasPlacedInLevel ? 1 : 0);
                    break;

                case SaveComponent component:
                    writer.WriteLengthPrefixedString(component.ParentEntityName);
                    break;

                default:
                    throw new NotImplementedException($"Unknown SaveObject kind {saveObject.ObjectKind}");
            }
        }

        public static void DeserializeObjectData(SaveObject saveObject, BinaryReader reader)
        {
            var dataLength = reader.ReadInt32();
            var before = reader.BaseStream.Position;

            switch (saveObject)
            {
                case SaveActor actor:
                    actor.ParentObject = reader.ReadObjectReference();
                    var componentCount = reader.ReadInt32();
                    for (int i = 0; i < componentCount; i++)
                        actor.Components.Add(reader.ReadObjectReference());

                    break;

                case SaveComponent _:
                    break;

                default:
                    throw new NotImplementedException($"Unknown SaveObject kind {saveObject.ObjectKind}");
            }

            SerializedProperty prop;
            while ((prop = DeserializeProperty(reader)) != null)
            {
                var (objProperty, _) = prop.GetMatchingSaveProperty(saveObject.GetType());

                if (objProperty == null)
                {
                    var type = saveObject.GetType();

                    var propType = prop.PropertyType;
                    if (prop is StructProperty structProp)
                        propType += $" ({structProp.Data.GetType().Name})";

                    var propertyUniqueName = $"{saveObject.TypePath}.{prop.PropertyName}:{propType}";
                    if (!missingProperties.Contains(propertyUniqueName))
                    {
                        if (type == typeof(SaveActor) || type == typeof(SaveComponent))
                            log.Warn($"Missing property for {propType} {prop.PropertyName} on {saveObject.TypePath}");
                        else
                            log.Warn($"Missing property for {propType} {prop.PropertyName} on {type.Name}");

                        missingProperties.Add(propertyUniqueName);
                    }

                    saveObject.AddDynamicProperty(prop);
                    continue;
                }

                prop.AssignToProperty(saveObject, objProperty);
            }

            var extra = reader.ReadInt32();
            if (extra != 0)
                log.Warn($"Read extra {extra} after {saveObject.TypePath} @ {reader.BaseStream.Position - 4}");

            var remaining = dataLength - (int)(reader.BaseStream.Position - before);
            if (remaining > 0)
                saveObject.DeserializeNativeData(reader, remaining);

            var after = reader.BaseStream.Position;
            if (before + dataLength != after)
                throw new FatalSaveException($"Expected {dataLength} bytes read but got {after - before}", before);
        }

        public static void SerializeObjectData(SaveObject saveObject, BinaryWriter writer)
        {
            // TODO: Replace this with proper size calculations
            using var ms = new MemoryStream();
            using var dataWriter = new BinaryWriter(ms);

            switch (saveObject)
            {
                case SaveActor actor:
                    dataWriter.Write(actor.ParentObject);
                    dataWriter.Write(actor.Components.Count);
                    foreach (var component in actor.Components)
                        dataWriter.Write(component);

                    break;

                case SaveComponent component:
                    break;

                default:
                    throw new NotImplementedException($"Unknown SaveObject kind {saveObject.ObjectKind}");
            }

            // TODO: serialize properties

            var bytes = ms.ToArray();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public static SerializedProperty DeserializeProperty(BinaryReader reader)
        {
            SerializedProperty result;

            var propertyName = reader.ReadLengthPrefixedString();
            if (propertyName == "None")
                return null;

            Trace.Assert(!String.IsNullOrEmpty(propertyName));

            var propertyType = reader.ReadLengthPrefixedString();
            var size = reader.ReadInt32();
            var index = reader.ReadInt32();

            int overhead = 1;
            var before = reader.BaseStream.Position;
            switch (propertyType)
            {
                case ArrayProperty.TypeName:
                    result = ArrayProperty.Parse(reader, propertyName, index, out overhead);
                    break;
                case BoolProperty.TypeName:
                    overhead = 2;
                    result = BoolProperty.Deserialize(reader, propertyName, index);
                    break;
                case ByteProperty.TypeName:
                    result = ByteProperty.Deserialize(reader, propertyName, index, out overhead);
                    break;
                case EnumProperty.TypeName:
                    result = EnumProperty.Deserialize(reader, propertyName, index, out overhead);
                    break;
                case FloatProperty.TypeName:
                    result = FloatProperty.Deserialize(reader, propertyName, index);
                    break;
                case IntProperty.TypeName:
                    result = IntProperty.Deserialize(reader, propertyName, index);
                    break;
                case Int64Property.TypeName:
                    result = Int64Property.Deserialize(reader, propertyName, index);
                    break;
                case InterfaceProperty.TypeName:
                    result = InterfaceProperty.Deserialize(reader, propertyName, index);
                    break;
                case MapProperty.TypeName:
                    result = MapProperty.Deserialize(reader, propertyName, index, out overhead);
                    break;
                case NameProperty.TypeName:
                    result = NameProperty.Deserialize(reader, propertyName, index);
                    break;
                case ObjectProperty.TypeName:
                    result = ObjectProperty.Deserialize(reader, propertyName, index);
                    break;
                case StrProperty.TypeName:
                    result = StrProperty.Deserialize(reader, propertyName, index);
                    break;
                case StructProperty.TypeName:
                    result = StructProperty.Deserialize(reader, propertyName, size, index, out overhead);
                    break;
                case TextProperty.TypeName:
                    result = TextProperty.Deserialize(reader, propertyName, index);
                    break;
                default:
                    throw new NotImplementedException($"Unknown property type {propertyType} for property {propertyName}");
            }
            var after = reader.BaseStream.Position;
            var readBytes = (int)(after - before - overhead);

            if (size != readBytes)
                throw new InvalidOperationException($"Expected {size} bytes read but got {readBytes}");

            return result;
        }

        public static void SerializeProperty(SerializedProperty prop, BinaryWriter writer)
        {
            writer.WriteLengthPrefixedString(prop.PropertyName);
            writer.WriteLengthPrefixedString(prop.PropertyType);
            writer.Write(prop.SerializedLength);
            writer.Write(prop.Index);

            prop.Serialize(writer);
        }

        public static List<ObjectReference> DeserializeDestroyedActors(BinaryReader reader)
        {
            var destroyedActorsCount = reader.ReadInt32();
            log.Info($"Save contains {destroyedActorsCount} destroyed actors");

            var list = new List<ObjectReference>();

            for (int i = 0; i < destroyedActorsCount; i++)
                list.Add(reader.ReadObjectReference());

            return list;
        }

        public static void SerializeDestroyedActors(List<ObjectReference> destroyedActors, BinaryWriter writer)
        {
            writer.Write(destroyedActors.Count);

            foreach (var reference in destroyedActors)
                writer.Write(reference);
        }
    }
}
