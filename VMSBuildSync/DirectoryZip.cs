using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ionic.Crc;
using Ionic.Zip;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ionic.Zlib;
using Renci.SshNet;

namespace VMSBuildSync
{
    public class DirectoryZip
    {
        public List<Tuple<Regex, VMSFileConfig>> FileAttributeList = new List<Tuple<Regex, VMSFileConfig>>();

        private static Regex newLineRegex = new Regex(@"\w*\$\s*$");
        private static Encoding WindowsEncoding = Encoding.GetEncoding(1252);
        //this should only be used for files in a directory, it will not behave properly if the files are in different or sub directories
        public void MakeZipForDirectory(string zipPath, List<FileStream> files,
            Dictionary<string, int> existingFileSizes, bool forceOverride)
        {
            using var archive = new ZipFile();

            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileName(file.Name);
                    var lastMatcher = FileAttributeList?.LastOrDefault(tpl => tpl.Item1.IsMatch(file.Name));
                    ZipEntry entry = null;
                    using var reader = new StreamReader(file, Encoding.ASCII);
                    var targetFileSize = file.Length;
                    int longestLine = 0;
                    var wholeFile = reader.ReadToEnd();

                    if (lastMatcher.Item2.RecordType == RecordTypes.C_STREAM)
                    {
                        targetFileSize = EncodeStreamBinary(existingFileSizes, forceOverride, archive, fileName, ref entry, wholeFile, ref longestLine);
                    }
                    else
                    {
                        var newlineSplit = Environment.NewLine;
                        if (newlineSplit != "\r\n" && wholeFile.Contains("\r\n"))
                        {
                            Logger.WriteLine(8, $"warning: {fileName} looks like it has wrong line endings for the platform");
                            newlineSplit = "\r\n";
                        }

                        var splitFile = wholeFile.Split(newlineSplit, StringSplitOptions.None);

                        if (wholeFile.Contains('\uFEFF'))
                            Logger.WriteLine(10, "BOM detected");

                        if (splitFile.Length < 3 && wholeFile.Length > 512)
                        {
                            lastMatcher = FileAttributeList?.LastOrDefault(tpl => tpl.Item1.IsMatch("this_is_a_binary_file"));
                            Logger.WriteLine(10, $"warning: {fileName} looks like it is either binary or has wrong line endings for the platform");
                        }
                        longestLine = splitFile.OrderByDescending(s => s.Length).FirstOrDefault()?.Length ?? 512;
                        
                        //no need to translate if its already the same
                        if (lastMatcher == null || ((string.IsNullOrEmpty(lastMatcher.Item2.LineEndingOverride) ||
                            lastMatcher.Item2.LineEndingOverride == Environment.NewLine) && lastMatcher.Item2.RecordType != RecordTypes.C_VARIABLE))
                        {
                            if (forceOverride || !(existingFileSizes.TryGetValue(fileName, out var existingSize) &&
                                  existingSize == file.Length))
                            {
                                entry = archive.AddFile(fileName);
                                entry.CompressionLevel = CompressionLevel.Level9;
                                entry.EmitTimesInWindowsFormatWhenSaving = false;
                            }
                        }
                        else if (lastMatcher.Item2.RecordType == RecordTypes.C_VARIABLE)
                        {
                            try
                            {
                                var translatedFile = MakeVariableRecord(splitFile);
                                targetFileSize = translatedFile.Length;
                                if (forceOverride || !(existingFileSizes.TryGetValue(fileName, out var existingSize) &&
                                      existingSize == translatedFile.Length))
                                {
                                    entry = archive.AddEntry(fileName, translatedFile);
                                    entry.IsText = false;
                                    entry.CompressionLevel = CompressionLevel.Level9;
                                    entry.EmitTimesInWindowsFormatWhenSaving = false;
                                }
                            }
                            catch
                            {
                                Logger.WriteLine(10, $"failed to encode variable record {fileName} fallback to STREAM");
                                lastMatcher = FileAttributeList?.LastOrDefault(tpl => tpl.Item1.IsMatch("this_is_a_binary_file"));
                                targetFileSize = EncodeStreamBinary(existingFileSizes, forceOverride, archive, fileName, ref entry, wholeFile, ref longestLine);
                            }
                        }
                        else
                        {
                            var translatedFile = string.Join(lastMatcher.Item2.LineEndingOverride, splitFile);
                            targetFileSize = translatedFile.Length;
                            if (forceOverride || !(existingFileSizes.TryGetValue(fileName, out var existingSize) &&
                                  existingSize == translatedFile.Length))
                            {
                                entry = archive.AddEntry(fileName, translatedFile, Encoding.ASCII);
                                entry.CompressionLevel = CompressionLevel.Level9;
                                entry.EmitTimesInWindowsFormatWhenSaving = false;
                            }
                        }
                    }

                    if (entry != null && lastMatcher != null)
                    {
                        entry._Extra = MakeVMSExtraBlock(
                            lastMatcher.Item2.RecordType,
                            lastMatcher.Item2.RecordLayout,
                            FileAttributes.M_IMPLIEDCC,
                            (ushort)longestLine,
                            (uint)targetFileSize,
                            0,
                            (ushort)lastMatcher.Item2.RecordSize,
                            0,
                            entry.CreationTime,
                            entry.ModifiedTime,
                            lastMatcher.Item2.OwnerId,
                            lastMatcher.Item2.System,
                            lastMatcher.Item2.Owner,
                            lastMatcher.Item2.Group,
                            lastMatcher.Item2.World
                        );
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(10, $"failed with {ex} while operating on {file.Name}");
                }
            }

            archive.Save(zipPath);
        }

        private static long EncodeStreamBinary(Dictionary<string, int> existingFileSizes, bool forceOverride, ZipFile archive, string fileName, ref ZipEntry entry, string wholeFile, ref int longestLine)
        {
            //STREAM is for non translated binary files
            //'Max line length' is capped at 512 but still needs to be present
            long targetFileSize = wholeFile.Length;
            if (forceOverride || !(existingFileSizes.TryGetValue(fileName, out var existingSize) &&
existingSize == wholeFile.Length))
            {
                longestLine = (int)Math.Min(targetFileSize, 512l);
                entry = archive.AddEntry(fileName, wholeFile, Encoding.ASCII);
                entry.CompressionLevel = CompressionLevel.Level9;
                entry.EmitTimesInWindowsFormatWhenSaving = false;
            }

            return targetFileSize;
        }

        private static MemoryStream MakeVariableRecord(string[] input)
        {
            var result = new MemoryStream();
            for(var i = 0; i < input.Length; i++)
            {
                var line = input[i];
                var lineBytes = WindowsEncoding.GetBytes(line);
                short lineLength = checked((short)(lineBytes.Length));
                if(lineLength == 0 && i == (input.Length - 1))
                {
                    break;
                }
                result.Write(BitConverter.GetBytes(lineLength));
                result.Write(lineBytes);
                if ((lineBytes.Length % 2) == 1)
                {
                    result.WriteByte(0);
                }
            }
            result.WriteByte(255);
            result.WriteByte(255);
            result.Seek(0, SeekOrigin.Begin);
            return result;
        }

        public string VMSifyPath(string unixyPath)
        {
            if (!unixyPath.StartsWith("/"))
                throw new Exception("unixy path conversion requires a rooted path");

            var driveSpecEnd = unixyPath.IndexOf("/", 1);
            var driveSpec = unixyPath.Substring(1, driveSpecEnd - 1);
            var remaining = unixyPath.Substring(driveSpecEnd + 1).Replace("/", ".");
            return string.Format("{0}:[{1}]", driveSpec, remaining);
        }

        public async Task UnpackRemoteZip(Func<string, Task> sshWriteLine, Func<Regex, Task<string>> sshExpect,
            SftpClient fileClient, FileStream fileStream, string remotePath, string rootPath)
        {
            Logger.WriteLine(2, $"unpacking zip {VMSifyPath(remotePath)} of size {fileStream.Length}");
            await RemoteSync.UploadFileAsync(fileClient, fileStream, rootPath + "/synchTemp.zip");
            await sshWriteLine(
                $"unzip \"-X\" \"-qq\" \"-o\" {VMSifyPath(rootPath)}synchTemp.zip \"-d\" {VMSifyPath(remotePath)}");
            var got = await sshExpect(newLineRegex);
            if (string.IsNullOrWhiteSpace(got))
                Logger.WriteLine(10, $"unzip operation timed out in {remotePath}");
            else
                Logger.WriteLine(1, got);

            await sshWriteLine($"delete {VMSifyPath(rootPath)}synchTemp.zip;*");
            var got2 = await sshExpect(newLineRegex);
            if (string.IsNullOrWhiteSpace(got2))
                Logger.WriteLine(10, $"delete after unzip operation timed out in {remotePath}");
            else
                Logger.WriteLine(1, got2);
        }

        public static byte MakeRecordType(RecordTypes type, RecordLayouts layout)
        {
            return (byte) ((int) type | (((int) layout) << 4));
        }

        public static byte[] MakeProtection(FileProtection system, FileProtection owner, FileProtection group,
            FileProtection world)
        {
            var result1 = MakeProtection(system) |
                          (MakeProtection(owner) << 4);
            var result2 = MakeProtection(group) |
                          (MakeProtection(world) << 4);
            return new byte[] {(byte) result1, (byte) result2};
        }

        public static uint MakeProtection(FileProtection protection)
        {
            return (uint) (((protection & FileProtection.Read) != 0 ? 0 : 1) |
                           ((protection & FileProtection.Write) != 0 ? 0 : 1 << 1) |
                           ((protection & FileProtection.Execute) != 0 ? 0 : 1 << 2) |
                           ((protection & FileProtection.Delete) != 0 ? 0 : 1 << 3));
        }

        public static unsafe DateTime ConvertFromSmithsonianTime(byte[] blob)
        {
            if (blob.Length < 8)
                throw new Exception("timeblob must be 8 bytes");

            fixed (byte* ptr = blob)
            {
                var nsLong = *(long*) ptr;
                var timeStart = new DateTime(1858, 11, 17, 0, 0, 0, DateTimeKind.Utc);
                return timeStart.AddTicks(nsLong);
            }
        }

        public static unsafe byte[] ConvertToSmithsonianTime(DateTime dt)
        {
            var smithsonianTicks = dt.Ticks - 586288800000000000;
            var result = new byte[8];
            fixed (byte* ptr = result)
            {
                *(long*) ptr = smithsonianTicks;
            }

            return result;
        }

        public static unsafe byte[] WriteHeader(PK_header header)
        {
            var bufferSize = 8; //initial fixed size of PK_header
            var crc = new CRC32();

            foreach (var field in header.data)
            {
                bufferSize += 4; //base field size
                bufferSize += field.size;
            }

            var buffer = new byte[bufferSize];

            fixed (byte* bufferPtr = buffer)
            {
                var cursor = bufferPtr;
                *(ushort*) cursor = header.tag;
                cursor += 2;
                *(ushort*) cursor = header.size;
                cursor += 2;
                //skip past the crc, well fill it in later
                int* crcPtr = (int*) cursor;
                cursor += 4;

                foreach (var field in header.data)
                {
                    *(ushort*) cursor = field.tag;
                    cursor += 2;
                    *(ushort*) cursor = field.size;
                    cursor += 2;
                    Marshal.Copy(field.value, 0, (IntPtr) cursor, field.value.Length);
                    cursor += field.value.Length;
                }

                crc.SlurpBlock(buffer, 8, bufferSize - 8);
                *crcPtr = crc.Crc32Result;
            }

            return buffer;
        }

        public static unsafe FatDef ReadFATDef(byte[] bytes)
        {
            FatDef result = new FatDef();
            fixed (byte* ptr = bytes)
            {
                result = *(FatDef*) ptr;
            }

            return result;
        }

        public static unsafe byte[] WriteFATDef(FatDef def)
        {
            var bytes = new byte[32];
            fixed (byte* ptr = bytes)
            {
                *(FatDef*) ptr = def;
            }

            return bytes;
        }

        public static unsafe PK_header ReadHeader(byte[] bytes)
        {
            PK_header header = new PK_header {data = new List<PK_field>()};

            fixed (byte* buffer = bytes)
            {
                byte* cursor = buffer;
                header.tag = *(ushort*) cursor;
                cursor += 2;
                header.size = *(ushort*) cursor;
                cursor += 2;
                byte* startOfData = cursor;
                header.crc32 = *(uint*) cursor;
                cursor += 4;

                while (cursor - startOfData < header.size)
                {
                    var field = new PK_field();
                    field.tag = *(ushort*) cursor;
                    cursor += 2;
                    field.size = *(ushort*) cursor;
                    cursor += 2;
                    field.value = new byte[field.size];
                    Marshal.Copy((IntPtr) cursor, field.value, 0, field.size);
                    cursor += field.size;
                    header.data.Add(field);
                }
            }

            return header;
        }

        private const int VMSAttributeHeader = 0xc;
        private const int VMSAttributeSize = 127;


        public static byte[] MakeVMSExtraBlock(RecordTypes recordType, RecordLayouts layout, FileAttributes attributes,
            ushort recordSize, uint fileSize, byte bucketSize, ushort maxRecordSize, ushort defaultExtend,
            DateTime created,
            DateTime modified, uint ownerId, FileProtection system, FileProtection owner, FileProtection group,
            FileProtection world)
        {
            var headerResult = new PK_header
                {tag = VMSAttributeHeader, size = VMSAttributeSize, data = new List<PK_field>()};
            var evenFileSize = Math.DivRem(fileSize, 512, out var fileSizeRemainder) + 1;
            FatDef fatDef = new FatDef
            {
                b_rtype = MakeRecordType(recordType, layout),
                b_rattrib = (byte) attributes,
                w_rsize = recordSize,
                l_hiblk = (uint)(((evenFileSize + 1) << 16) | ((evenFileSize + 1) >> 16)), 
                l_efblk = (uint)((evenFileSize << 16) | (evenFileSize >> 16)),
                w_ffbyte = (ushort)fileSizeRemainder,
                b_bktsize = bucketSize,
                b_vfcsize = (byte)(recordType == RecordTypes.C_VFC ? 2 : 0),
                w_maxrec = maxRecordSize,
                w_defext = defaultExtend,
                w_gbc = 0
            };
            headerResult.data.Add(new PK_field {size = 32, tag = 4, value = WriteFATDef(fatDef)});
            headerResult.data.Add(new PK_field {size = 4, tag = 3, value = BitConverter.GetBytes((int) 0)});
            headerResult.data.Add(new PK_field {size = 8, tag = 17, value = ConvertToSmithsonianTime(created)});
            headerResult.data.Add(new PK_field {size = 8, tag = 18, value = ConvertToSmithsonianTime(modified)});
            headerResult.data.Add(new PK_field {size = 8, tag = 19, value = BitConverter.GetBytes((long) 0)});
            headerResult.data.Add(new PK_field {size = 8, tag = 20, value = BitConverter.GetBytes((long) 0)});
            headerResult.data.Add(new PK_field {size = 2, tag = 13, value = new byte[] {1, 0}});
            headerResult.data.Add(new PK_field {size = 4, tag = 21, value = BitConverter.GetBytes(ownerId)});
            headerResult.data.Add(
                new PK_field {size = 2, tag = 22, value = MakeProtection(system, owner, group, world)});
            headerResult.data.Add(new PK_field {size = 2, tag = 23, value = new byte[] {0, 0}});
            headerResult.data.Add(new PK_field {size = 1, tag = 29, value = new byte[] {0}});

            return WriteHeader(headerResult);
        }

        //public ZipArchive GetRemoteZip(ShellStream sshClient, SftpClient fileClient, string remotePath)
        //{
        //    sshClient.WriteLine($"zip \"V\" sychTemp.zip {remotePath}");
        //}
    }

    public class VMSFileConfig
    {
        public RecordTypes RecordType;
        public RecordLayouts RecordLayout;
        public FileProtection System;
        public FileProtection Owner;
        public FileProtection Group;
        public FileProtection World;
        public int RecordSize;
        public uint OwnerId;
        public string LineEndingOverride;

        public static VMSFileConfig MakeStreamLF(FileProtection protection, uint ownerId)
        {
            return new VMSFileConfig
            {
                RecordType = RecordTypes.C_STREAMLF,
                RecordLayout = RecordLayouts.C_SEQUENTIAL,
                System = FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete,
                Owner = protection,
                Group = protection,
                World = FileProtection.Read,
                RecordSize = 0,
                LineEndingOverride = "\n",
                OwnerId = ownerId
            };
        }

        public static VMSFileConfig MakeStreamCR(FileProtection protection, uint ownerId)
        {
            return new VMSFileConfig
            {
                RecordType = RecordTypes.C_STREAMCR,
                RecordLayout = RecordLayouts.C_SEQUENTIAL,
                System = FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete,
                Owner = protection,
                Group = protection,
                World = FileProtection.Read,
                RecordSize = 0,
                LineEndingOverride = "\r",
                OwnerId = ownerId
            };
        }

        public static VMSFileConfig MakeStream(FileProtection protection, uint ownerId)
        {
            return new VMSFileConfig
            {
                RecordType = RecordTypes.C_STREAM,
                RecordLayout = RecordLayouts.C_SEQUENTIAL,
                System = FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete,
                Owner = protection,
                Group = protection,
                World = FileProtection.Read,
                RecordSize = 0,
                LineEndingOverride = "\r",
                OwnerId = ownerId
            };
        }

        public static VMSFileConfig MakeVar(FileProtection protection, uint ownerId)
        {
            return new VMSFileConfig
            {
                RecordType = RecordTypes.C_VARIABLE,
                RecordLayout = RecordLayouts.C_SEQUENTIAL,
                System = FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete,
                Owner = protection,
                Group = protection,
                World = FileProtection.Read,
                RecordSize = 0,
                LineEndingOverride = "\r",
                OwnerId = ownerId
            };
        }

        public static unsafe uint MakeUIC(uint ownerId, uint groupId)
        {
            uint result = (groupId << 16) | ownerId;
            return result;
        }
        const uint UIC_V_MEMBER = 0;
        const uint UIC_S_MEMBER = 16;
        const uint UIC_M_MEMBER = 0x0000FFFF;
        const uint UIC_V_GROUP = 16;
        const uint UIC_S_GROUP = 14;
        const uint UIC_M_GROUP = 0x3FFF0000;
        const uint UIC_V_FORMAT = 30;
        const uint UIC_S_FORMAT = 2;
        const uint UIC_M_FORMAT = 0xC0000000;
        const uint UIC_V_ID_CODE = 0;
        const uint UIC_S_ID_CODE = 28;
        const uint UIC_M_ID_CODE = 0x0FFFFFFF;
    }

    
    
    public enum RecordTypes
    {
        C_UNDEFINED = 0,
        C_FIXED = 1,
        C_VARIABLE = 2,
        C_VFC = 3,
        C_STREAM = 4,
        C_STREAMLF = 5,
        C_STREAMCR = 6,
    }

    public enum RecordLayouts
    {
        C_SEQUENTIAL = 0,
        C_RELATIVE = 1,
        C_INDEXED = 2,
        C_DIRECT = 3,
    }

    [Flags]
    public enum FileAttributes
    {
        V_FORTRANCC = 0,
        M_FORTRANCC = 1,
        V_IMPLIEDCC = 1,
        M_IMPLIEDCC = 2,
        V_PRINTCC = 2,
        M_PRINTCC = 4,
        V_NOSPAN = 3,
        M_NOSPAN = 8,
        V_MSBRCW = 4,
        M_MSBRCW = 16
    }

    [Flags]
    public enum FileProtection
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4,
        Delete = 8
    }
    
    public struct PK_field
    {
        public ushort tag;
        public ushort size;
        public byte[] value;
    };

    public struct PK_header
    {
        public ushort tag;
        public ushort size;
        public uint crc32;
        public List<PK_field> data;
    };

    //converted from info zip header
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct FatDef
    {
        public byte b_rtype; /* record type                      */
        public byte b_rattrib; /* record attributes                */
        public ushort w_rsize; /* record size in bytes             */
        public uint l_hiblk; /* highest allocated VBN            actually stored as 16bits high little endian, followed by 16bits low little endian*/
        public uint l_efblk; /* end of file VBN                  actually stored as 16bits high little endian, followed by 16bits low little endian*/
        public ushort w_ffbyte; /* first free byte in EFBLK         */
        public byte b_bktsize; /* bucket size in blocks            */
        public byte b_vfcsize; /* # of control bytes in VFC record */
        public ushort w_maxrec; /* maximum record size in bytes     */
        public ushort w_defext; /* default extend quantity          */
        public ushort w_gbc; /* global buffer count              */
        public ulong fill;
        public ushort w_versions;
    }
}