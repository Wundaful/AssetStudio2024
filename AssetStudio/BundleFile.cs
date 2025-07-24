﻿using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace AssetStudio
{
    [Flags]
    public enum ArchiveFlags
    {
        CompressionTypeMask = 0x3f,
        BlocksAndDirectoryInfoCombined = 0x40,
        BlocksInfoAtTheEnd = 0x80,
        OldWebPluginCompatibility = 0x100,
        BlockInfoNeedPaddingAtStart = 0x200
    }

    [Flags]
    public enum CnEncryptionFlags
    {
        V1 = 0x200,
        V2_V3 = 0x1400,
    }

    [Flags]
    public enum StorageBlockFlags
    {
        CompressionTypeMask = 0x3f,
        Streamed = 0x40
    }

    public enum CompressionType
    {
        Auto = -1,
        None,
        Lzma,
        Lz4,
        Lz4HC,
        Lzham,
        Zstd, //custom
        Oodle, //custom
    }

    public class BundleFile
    {
        public readonly bool IsDataAfterBundle;

        public class Header
        {
            public string signature;
            public uint version;
            public string unityVersion;
            public UnityVersion unityRevision;
            public long size;
            public uint compressedBlocksInfoSize;
            public uint uncompressedBlocksInfoSize;
            public ArchiveFlags flags;
        }

        public class StorageBlock
        {
            public uint compressedSize;
            public uint uncompressedSize;
            public StorageBlockFlags flags;
        }

        public class Node
        {
            public long offset;
            public long size;
            public uint flags;
            public string path;
        }

        public Header m_Header;
        private StorageBlock[] m_BlocksInfo;
        private Node[] m_DirectoryInfo;

        public List<StreamFile> fileList;

        public BundleFile(FileReader reader, CompressionType customBlockInfoCompression, CompressionType customBlockCompression, UnityVersion specUnityVer = null, bool isMultiBundle = false)
        {
            Stream blocksStream;
            m_Header = new Header();
            m_Header.signature = reader.ReadStringToNull();
            m_Header.version = reader.ReadUInt32();
            m_Header.unityVersion = reader.ReadStringToNull();
            var revStr = reader.ReadStringToNull();
            if (!UnityVersion.TryParse(revStr, out m_Header.unityRevision))
                m_Header.unityRevision = new UnityVersion();
            
            switch (m_Header.signature)
            {
                case "UnityArchive":
                    break; //TODO
                case "UnityWeb":
                case "UnityRaw":
                    if (m_Header.version == 6)
                    {
                        goto case "UnityFS";
                    }
                    ReadHeaderAndBlocksInfo(reader);
                    using (reader)
                    {
                        blocksStream = ReadBlocksAndDirectory(reader);
                    }
                    ReadFiles(blocksStream);
                    break;
                case "UnityFS":
                    ReadHeader(reader);

                    var bundleSize = m_Header.size;
                    var streamSize = reader.BaseStream.Length;
                    if (bundleSize > streamSize)
                    {
                        Logger.Warning("Bundle size is incorrect.");
                    }
                    else if (streamSize - bundleSize > 200)
                    {
                        IsDataAfterBundle = true;
                    }
                    
                    var unityVer = m_Header.unityRevision;
                    if (specUnityVer != null)
                    {
                        if (!unityVer.IsStripped && specUnityVer != unityVer)
                        {
                            Logger.Warning($"Detected Unity version is different from the specified one ({specUnityVer.FullVersion.Color(ColorConsole.BrightCyan)}).\n" +
                                $"Assets may load with errors.\n" +
                                $"It is recommended to specify the detected Unity version: {unityVer.FullVersion.Color(ColorConsole.BrightCyan)}");
                        }
                        unityVer = specUnityVer;
                    }

                    UnityCnCheck(reader, customBlockInfoCompression, unityVer);
                    
                    ReadBlocksInfoAndDirectory(reader, customBlockInfoCompression, unityVer);

                    if (!isMultiBundle && IsUncompressedBundle)
                    {
                        ReadFiles(reader.BaseStream, reader.Position);
                        break;
                    }
                    
                    blocksStream = ReadBlocks(reader, customBlockCompression);
                    ReadFiles(blocksStream);
                    
                    if (!IsDataAfterBundle)
                        reader.Close();

                    break;
            }
        }

        private void ReadHeaderAndBlocksInfo(FileReader reader)
        {
            if (m_Header.version >= 4)
            {
                var hash = reader.ReadBytes(16);
                var crc = reader.ReadUInt32();
            }
            var minimumStreamedBytes = reader.ReadUInt32();
            m_Header.size = reader.ReadUInt32();
            var numberOfLevelsToDownloadBeforeStreaming = reader.ReadUInt32();
            var levelCount = reader.ReadInt32();
            m_BlocksInfo = new StorageBlock[1];
            for (int i = 0; i < levelCount; i++)
            {
                var storageBlock = new StorageBlock
                {
                    compressedSize = reader.ReadUInt32(),
                    uncompressedSize = reader.ReadUInt32(),
                };
                if (i == levelCount - 1)
                {
                    m_BlocksInfo[0] = storageBlock;
                }
            }
            if (m_Header.version >= 2)
            {
                var completeFileSize = reader.ReadUInt32();
            }
            if (m_Header.version >= 3)
            {
                var fileInfoHeaderSize = reader.ReadUInt32();
            }
            reader.Position = m_Header.size;
        }

        private Stream CreateBlocksStream(string path)
        {
            var uncompressedSizeSum = m_BlocksInfo.Sum(x => x.uncompressedSize);
            return uncompressedSizeSum >= int.MaxValue
                ? new FileStream(path + ".temp", FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose)
                : (Stream) new MemoryStream((int)uncompressedSizeSum);
        }

        private Stream ReadBlocksAndDirectory(FileReader reader)
        {
            var blocksStream = CreateBlocksStream(reader.FullPath);
            var isCompressed = m_Header.signature == "UnityWeb";
            foreach (var blockInfo in m_BlocksInfo)
            {
                var uncompressedBytes = reader.ReadBytes((int)blockInfo.compressedSize);
                if (isCompressed)
                {
                    using (var memoryStream = new MemoryStream(uncompressedBytes))
                    {
                        using (var decompressStream = BundleDecompressionHelper.DecompressLzmaStream(memoryStream))
                        {
                            uncompressedBytes = decompressStream.ToArray();
                        }
                    }
                }
                blocksStream.Write(uncompressedBytes, 0, uncompressedBytes.Length);
            }
            blocksStream.Position = 0;

            var blocksReader = new EndianBinaryReader(blocksStream);
            var nodesCount = blocksReader.ReadInt32();
            m_DirectoryInfo = new Node[nodesCount];
            for (var i = 0; i < nodesCount; i++)
            {
                m_DirectoryInfo[i] = new Node
                {
                    path = blocksReader.ReadStringToNull(),
                    offset = blocksReader.ReadUInt32(),
                    size = blocksReader.ReadUInt32()
                };
            }

            return blocksStream;
        }

        private void ReadFiles(Stream inputStream, long blocksOffset = 0)
        {
            fileList = new List<StreamFile>(m_DirectoryInfo.Length);
            foreach (var node in m_DirectoryInfo)
            {
                var file = new StreamFile();
                fileList.Add(file);
                file.path = node.path;
                file.fileName = Path.GetFileName(node.path);
                try
                {
                    file.stream = new OffsetStream(inputStream, node.offset + blocksOffset, node.size);
                }
                catch (IOException e)
                {
                    Logger.Warning($"Failed to access {file.fileName} file.\n{e}");
                }
            }
        }

        private void ReadHeader(FileReader reader)
        {
            m_Header.size = reader.ReadInt64();
            m_Header.compressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.uncompressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.flags = (ArchiveFlags)reader.ReadUInt32();
            if (m_Header.signature != "UnityFS")
            {
                reader.ReadByte();
            }
        }

        private void ReadBlocksInfoAndDirectory(FileReader reader, CompressionType customBlockInfoCompression, UnityVersion unityVer, bool silent = false)
        {
            byte[] blocksInfoBytes;

            if (m_Header.version >= 7)
            {
                reader.AlignStream(16);
            }
            else if (unityVer >= (2019, 4) && m_Header.flags != ArchiveFlags.BlocksAndDirectoryInfoCombined)
            {
                //check if we need to align the reader
                //- align to 16 bytes and check if all are 0
                //- if not, reset the reader to the previous position
                var preAlign = reader.Position;
                var alignData = reader.ReadBytes((16 - (int)(preAlign % 16)) % 16);
                if (alignData.Any(x => x != 0))
                {
                    reader.Position = preAlign;
                }
            }

            var compressedSize = (int)m_Header.compressedBlocksInfoSize;
            var uncompressedSize = (int)m_Header.uncompressedBlocksInfoSize;
            if (uncompressedSize < 0 || compressedSize < 0 || compressedSize > reader.BaseStream.Length)
            {
                throw new IOException("Incorrect blockInfo length.\nBlockInfo sizes might be encrypted.\n");
            }

            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
            {
                var position = reader.Position;
                reader.Position = m_Header.size - compressedSize;
                blocksInfoBytes = reader.ReadBytes(compressedSize);
                reader.Position = position;
            }
            else //0x40 BlocksAndDirectoryInfoCombined
            {
                blocksInfoBytes = reader.ReadBytes(compressedSize);
            }
            
            var compressionType = (CompressionType)(m_Header.flags & ArchiveFlags.CompressionTypeMask);
            if (customBlockInfoCompression == CompressionType.Auto)
            {
                if (!silent && compressionType > CompressionType.Lzham && Enum.IsDefined(typeof(CompressionType), compressionType))
                {
                    Logger.Warning($"Non-standard blockInfo compression type: {(int)compressionType}. Trying to decompress as {compressionType} archive..");
                }
            }
            else if (compressionType != CompressionType.None)
            {
                compressionType = customBlockInfoCompression;
                if (!silent)
                {
                    Logger.Info($"Custom blockInfo compression type: {customBlockInfoCompression}");
                }
            }
            Logger.Debug($"BlockInfo compression: {compressionType}");

            int numWrite;
            var errorMsg = string.Empty;
            MemoryStream blocksInfoUncompressedStream;
            switch (compressionType)
            {
                case CompressionType.None:
                {
                    blocksInfoUncompressedStream = new MemoryStream(blocksInfoBytes);
                    numWrite = compressedSize;
                    break;
                }
                case CompressionType.Lzma:
                {
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedSize);
                    using (var blocksInfoCompressedStream = new MemoryStream(blocksInfoBytes))
                    {
                        numWrite = (int)BundleDecompressionHelper.DecompressLzmaStream(blocksInfoCompressedStream, blocksInfoUncompressedStream, compressedSize, uncompressedSize, ref errorMsg);
                    }
                    blocksInfoUncompressedStream.Position = 0;
                    break;
                }
                case CompressionType.Lz4:
                case CompressionType.Lz4HC:
                case CompressionType.Zstd:
                case CompressionType.Oodle:
                {
                    var uncompressedBytes = new byte[uncompressedSize];
                    numWrite = BundleDecompressionHelper.DecompressBlock(compressionType, blocksInfoBytes, uncompressedBytes, ref errorMsg);
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedBytes);
                    break;
                }
                case CompressionType.Lzham:
                    throw new IOException($"Unsupported blockInfo compression type: {compressionType}.\n");
                default:
                    throw new IOException($"Unknown blockInfo compression type: {compressionType}.\nYou may try to specify the compression type manually.\n");
            }

            if (numWrite != uncompressedSize)
            {
                var msg = $"{compressionType} blockInfo decompression error. {errorMsg}\nWrite {numWrite} bytes but expected {uncompressedSize} bytes.";
                var exMsg = compressionType > CompressionType.Lz4HC || customBlockInfoCompression != CompressionType.Auto
                    ? "Wrong compression type or blockInfo data might be encrypted."
                    : "BlockInfo data might be encrypted.";
                throw new IOException($"{msg}\n{exMsg}\n");
            }

            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream))
            {
                var uncompressedDataHash = blocksInfoReader.ReadBytes(16);
                var blocksInfoCount = blocksInfoReader.ReadInt32();
                m_BlocksInfo = new StorageBlock[blocksInfoCount];
                for (var i = 0; i < blocksInfoCount; i++)
                {
                    m_BlocksInfo[i] = new StorageBlock
                    {
                        uncompressedSize = blocksInfoReader.ReadUInt32(),
                        compressedSize = blocksInfoReader.ReadUInt32(),
                        flags = (StorageBlockFlags)blocksInfoReader.ReadUInt16()
                    };
                }

                var nodesCount = blocksInfoReader.ReadInt32();
                m_DirectoryInfo = new Node[nodesCount];
                for (var i = 0; i < nodesCount; i++)
                {
                    m_DirectoryInfo[i] = new Node
                    {
                        offset = blocksInfoReader.ReadInt64(),
                        size = blocksInfoReader.ReadInt64(),
                        flags = blocksInfoReader.ReadUInt32(),
                        path = blocksInfoReader.ReadStringToNull(),
                    };
                }
            }
            if ((m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                reader.AlignStream(16);
            }
        }

        private Stream ReadBlocks(FileReader reader, CompressionType customBlockCompression)
        {
            var blocksStream = CreateBlocksStream(reader.FullPath);
            var blocksCompression = m_BlocksInfo.Max(x => (CompressionType)(x.flags & StorageBlockFlags.CompressionTypeMask));
            Logger.Debug($"BlockData compression: {blocksCompression}");
            Logger.Debug($"BlockData count: {m_BlocksInfo.Length}");

            if (customBlockCompression == CompressionType.Auto)
            {
                if (blocksCompression > CompressionType.Lzham && Enum.IsDefined(typeof(CompressionType), blocksCompression))
                {
                    Logger.Warning($"Non-standard block compression type: {(int)blocksCompression}. Trying to decompress as {blocksCompression} archive..");
                }
            }
            else
            {
                Logger.Info($"Custom block compression type: {customBlockCompression}");
                blocksCompression = customBlockCompression;
            }

            byte[] sharedCompressedBuff = null;
            byte[] sharedUncompressedBuff = null;
            if (blocksCompression != CompressionType.Lzma && blocksCompression != CompressionType.Lzham)
            {
                var blockSize = (int)m_BlocksInfo.Max(x => x.uncompressedSize);
                Logger.Debug($"BlockSize: {blockSize}");

                sharedCompressedBuff = BigArrayPool<byte>.Shared.Rent(blockSize);
                sharedUncompressedBuff = BigArrayPool<byte>.Shared.Rent(blockSize);
            }

            try
            {
                foreach (var blockInfo in m_BlocksInfo)
                {
                    var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);

                    if (customBlockCompression != CompressionType.Auto && compressionType > 0)
                    {
                        compressionType = customBlockCompression;
                    }

                    long numWrite;
                    var errorMsg = string.Empty;
                    switch (compressionType)
                    {
                        case CompressionType.None:
                            reader.BaseStream.CopyTo(blocksStream, blockInfo.compressedSize);
                            numWrite = blockInfo.compressedSize;
                            break;
                        case CompressionType.Lzma:
                            Logger.Info("Decompressing LZMA stream...");
                            numWrite = BundleDecompressionHelper.DecompressLzmaStream(reader.BaseStream, blocksStream, blockInfo.compressedSize, blockInfo.uncompressedSize, ref errorMsg);
                            break;
                        case CompressionType.Lz4:
                        case CompressionType.Lz4HC:
                        case CompressionType.Zstd:
                        case CompressionType.Oodle:
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            sharedCompressedBuff.AsSpan().Clear();
                            sharedUncompressedBuff.AsSpan().Clear();

                            _ = reader.Read(sharedCompressedBuff, 0, compressedSize);
                            var compressedSpan = new ReadOnlySpan<byte>(sharedCompressedBuff, 0, compressedSize);
                            var uncompressedSpan = new Span<byte>(sharedUncompressedBuff, 0, uncompressedSize);

                            numWrite = BundleDecompressionHelper.DecompressBlock(compressionType, compressedSpan, uncompressedSpan, ref errorMsg);
                            if (numWrite == uncompressedSize)
                                blocksStream.Write(sharedUncompressedBuff, 0, uncompressedSize);

                            break;
                        case CompressionType.Lzham:
                            throw new IOException($"Unsupported block compression type: {compressionType}.\n");
                        default:
                            throw new IOException($"Unknown block compression type: {compressionType}.\nYou may try to specify the compression type manually.\n");
                    }

                    if (numWrite != blockInfo.uncompressedSize)
                    {
                        var msg = $"{compressionType} block decompression error. {errorMsg}\nWrite {numWrite} bytes but expected {blockInfo.uncompressedSize} bytes.";
                        var exMsg = compressionType > CompressionType.Lz4HC || customBlockCompression != CompressionType.Auto
                            ? "Wrong compression type or block data might be encrypted."
                            : "Block data might be encrypted.";
                        throw new IOException($"{msg}\n{exMsg}\n");
                    }
                }
            }
            finally
            {
                if (sharedCompressedBuff != null)
                    BigArrayPool<byte>.Shared.Return(sharedCompressedBuff, clearArray: true);
                
                if (sharedUncompressedBuff != null)
                    BigArrayPool<byte>.Shared.Return(sharedUncompressedBuff, clearArray: true);
            }

            return blocksStream;
        }

        private void UnityCnCheck(FileReader reader, CompressionType customBlockInfoCompression, UnityVersion unityVer)
        {
            var hasUnityCnFlag = false;
            if (!unityVer.IsStripped)
            {
                // https://issuetracker.unity3d.com/issues/files-within-assetbundles-do-not-start-on-aligned-boundaries-breaking-patching-on-nintendo-switch
                if (unityVer < 2020
                    || unityVer.IsInRange(2020, (2020, 3, 34))
                    || unityVer.IsInRange(2021, (2021, 3, 2))
                    || unityVer.IsInRange(2022, (2022, 1, 1)))
                {
                    hasUnityCnFlag = ((CnEncryptionFlags)m_Header.flags & CnEncryptionFlags.V1) != 0;
                }
                else
                {
                    hasUnityCnFlag = ((CnEncryptionFlags)m_Header.flags & CnEncryptionFlags.V2_V3) != 0;
                }
            }
            if (!hasUnityCnFlag)
                return;

            var pos = reader.Position;
            reader.Position += 70;
            try
            {
                ReadBlocksInfoAndDirectory(reader, customBlockInfoCompression, unityVer, silent: true);
            }
            catch (Exception)
            {
                reader.Position = pos;
                return;
            }
            throw new NotSupportedException("Unsupported bundle file. UnityCN encryption was detected.");
        }

        private bool IsUncompressedBundle => m_BlocksInfo.All(x => (CompressionType)(x.flags & StorageBlockFlags.CompressionTypeMask) == CompressionType.None);
    }
}
