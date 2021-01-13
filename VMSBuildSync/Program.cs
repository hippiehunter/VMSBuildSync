using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ionic.Zip;

namespace VMSBuildSync
{
    class Program
    {
        public static string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);
            foreach (byte b in ba)
                hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }

        static async Task Main(string[] args)
        {
            if (args.Length < 8)
            {
                Console.WriteLine("\nusage: vmsbuildsync <host> <username> <password> <localRootDir> <remoteRootDir> <searchPattern> <forceupdate> <vmsUIC> [<logfile> [<loglevel>]]");
                Console.WriteLine("\n\n    File types or folders can be excluded via a JSON file named exclusions.json in the current folder:\n");
                Console.WriteLine("    {");
                Console.WriteLine("      \"ftypes\": [");
                Console.WriteLine("        \".exe\",");
                Console.WriteLine("        \".txt\"");
                Console.WriteLine("      ],");
                Console.WriteLine("      \"directories]\": [");
                Console.WriteLine("        \"exe\",");
                Console.WriteLine("        \"exludes\"");
                Console.WriteLine("      ]");
                Console.WriteLine("    }");

            }
            else
            {
                if (args.Length > 8)
                {
                    //log everything
                    int logLevel = -1;
                    if (args.Length > 9)
                        int.TryParse(args[9], out logLevel);

                    Logger.Init(args[8], logLevel, Debugger.IsAttached || logLevel == -1);
                }

                //Attempt to parse the OCTAL UIC values into DECIMAL parts
                Logger.WriteLine(10, $"  UIC={string.Join(' ', args)}");
                if (!uicToDecimal(args[7], out var uicGroup, out var uicUser))
                {
                    Logger.WriteLine(10, $"Failed to parse UIC! Must be in [x,y] format where x and y are positive octal numbers.");
                }
                else
                {
                    Logger.WriteLine(10, String.Format("UIC {0} => Group {1} (decimal), User {2} (decimal).",args[7], uicGroup, uicUser));

                    var regexs = new List<Tuple<Regex, VMSFileConfig>>();

                    regexs.Add(Tuple.Create(new Regex("\\w"), VMSFileConfig.MakeVar(FileProtection.Read | FileProtection.Write | FileProtection.Delete,
                        VMSFileConfig.MakeUIC(uicUser, uicGroup))));
                    regexs.Add(Tuple.Create(new Regex("\\.(com|cfw)$"), VMSFileConfig.MakeVar(FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete,
                        VMSFileConfig.MakeUIC(uicUser, uicGroup))));
                    regexs.Add(Tuple.Create(new Regex("\\.(gif|tlb|res|jpg|png)$"), VMSFileConfig.MakeStream(FileProtection.Read | FileProtection.Write | FileProtection.Delete,
                        VMSFileConfig.MakeUIC(uicUser, uicGroup))));
                    regexs.Add(Tuple.Create(new Regex("((encrypt.|enc1011)\\.dat)$"), VMSFileConfig.MakeStream(FileProtection.Read | FileProtection.Write | FileProtection.Delete,
                        VMSFileConfig.MakeUIC(uicUser, uicGroup))));
                    regexs.Add(Tuple.Create(new Regex("(synxml\\/test7(0|1)\\.txt)$"), VMSFileConfig.MakeStream(FileProtection.Read | FileProtection.Write | FileProtection.Delete,
                        VMSFileConfig.MakeUIC(uicUser, uicGroup))));
                    regexs.Add(Tuple.Create(new Regex("((baseline\\/test64|baseline\\/test70|baseline\\/test71)\\.xml)$"), VMSFileConfig.MakeStream(FileProtection.Read | FileProtection.Write | FileProtection.Delete,
                        VMSFileConfig.MakeUIC(uicUser, uicGroup))));
                    regexs.Add(Tuple.Create(new Regex("this_is_a_binary_file"), VMSFileConfig.MakeStream(FileProtection.Read | FileProtection.Write | FileProtection.Delete,
                        VMSFileConfig.MakeUIC(uicUser, uicGroup))));

                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                    //diagnostic code for dealing with VMS extra blocks
                    //var zippy = ZipFile.Read(@"C:\Users\hippi\Desktop\zippy.zip");
                    //foreach (var entry in zippy.Entries)
                    //{
                    //    var header = DirectoryZip.ReadHeader(entry._Extra);
                    //    var createdTime = header.data.FirstOrDefault(fld => fld.tag == 17);//find created time block stored as 100ns ticks starting with November 17, 1858
                    //    var modifiedTime = header.data.FirstOrDefault(fld => fld.tag == 18);//find modified time block stored as 100ns ticks starting with November 17, 1858
                    //    var fatField = header.data.FirstOrDefault(fld => fld.tag == 4);//find the fat block
                    //    var fatDef = DirectoryZip.ReadFATDef(fatField.value);
                    //    var bytes = DirectoryZip.WriteHeader(header);
                    //    if(!Enumerable.SequenceEqual(bytes, entry._Extra))
                    //        throw new Exception();
                    //
                    //    var modifiedTs = DirectoryZip.ConvertFromSmithsonianTime(modifiedTime.value);
                    //    var createdTs = DirectoryZip.ConvertFromSmithsonianTime(createdTime.value);
                    //    
                    //    
                    //    var myExtra = DirectoryZip.MakeVMSExtraBlock(RecordTypes.C_STREAMLF, RecordLayouts.C_SEQUENTIAL, FileAttributes.M_IMPLIEDCC, 
                    //        512, (uint)entry.UncompressedSize, 0, 32767, 0, DateTime.Now, 
                    //        DateTime.Now, 1, FileProtection.Read | FileProtection.Write | FileProtection.Delete, 
                    //        FileProtection.Read | FileProtection.Write | FileProtection.Delete, FileProtection.Read | FileProtection.Write | FileProtection.Delete, FileProtection.Read);
                    //    
                    //    var myExtra2 = DirectoryZip.MakeVMSExtraBlock(RecordTypes.C_STREAMLF, RecordLayouts.C_SEQUENTIAL, FileAttributes.M_IMPLIEDCC, 
                    //        512, (uint)entry.UncompressedSize, 0, 32767, 0, DateTime.Now, 
                    //        DateTime.Now, 1, FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete, 
                    //        FileProtection.Read , FileProtection.Read, FileProtection.Read);
                    //    
                    //    var myExtra3 = DirectoryZip.MakeVMSExtraBlock(RecordTypes.C_STREAMLF, RecordLayouts.C_SEQUENTIAL, FileAttributes.M_IMPLIEDCC, 
                    //        512, (uint)entry.UncompressedSize, 0, 32767, 0, DateTime.Now, 
                    //        DateTime.Now, 1, FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete, 
                    //        FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete, FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete, FileProtection.Read);
                    //    
                    //    var myExtra4 = DirectoryZip.MakeVMSExtraBlock(RecordTypes.C_STREAMLF, RecordLayouts.C_SEQUENTIAL, FileAttributes.M_IMPLIEDCC, 
                    //        512, (uint)entry.UncompressedSize, 0, 32767, 0, DateTime.Now, 
                    //        DateTime.Now, 1, FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete, 
                    //        FileProtection.Read | FileProtection.Write, FileProtection.Read, FileProtection.Read);
                    //    
                    //    var myExtra5 = DirectoryZip.MakeVMSExtraBlock(RecordTypes.C_STREAMLF, RecordLayouts.C_SEQUENTIAL, FileAttributes.M_IMPLIEDCC, 
                    //        512, (uint)entry.UncompressedSize, 0, 32767, 0, DateTime.Now, 
                    //        DateTime.Now, 1, FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete, 
                    //        FileProtection.Read | FileProtection.Write | FileProtection.Execute | FileProtection.Delete, FileProtection.Read | FileProtection.Execute, FileProtection.None);
                    //
                    //    
                    //    Console.WriteLine(ByteArrayToString(myExtra));
                    //    Console.WriteLine(ByteArrayToString(myExtra2));
                    //    Console.WriteLine(ByteArrayToString(myExtra3));
                    //    Console.WriteLine(ByteArrayToString(myExtra4));
                    //    Console.WriteLine(ByteArrayToString(myExtra5));
                    //    Console.WriteLine(ByteArrayToString(entry._Extra));
                    //}
                    //zippy.Save();
                    //zippy.ReadStream.Seek(0, SeekOrigin.Begin);
                    //var zipContents = new StreamReader(zippy.ReadStream).ReadToEnd();
                    //zippy.ExtractAll(@"C:\Users\hippi\RiderProjects\VMSBuildSync\jeffzip");

                    Logger.WriteLine(10, $"startup event({Process.GetCurrentProcess().Id}), args were {string.Join(' ', args)}");

                    TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
                    using (var remoteSync = new RemoteSync(args[0], args[1], args[2], args[3], args[4], args[5], regexs, args[6], tcs))
                    {
                        Console.WriteLine("Press Ctrl+C to exit remote sftp sync");

                        Console.CancelKeyPress += (sender, args) =>
                        {
                            tcs.TrySetResult(true);
                        };

                        AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
                        {
                            tcs.TrySetResult(true);
                        };

                        await tcs.Task;
                    }

                    Logger.WriteLine(10, $"shutdown event({Process.GetCurrentProcess().Id})");
                }
            }
        }

        /// <summary>
        /// Parses a numeric UIC string (e.g. [200,1]) containing OCTAL group and user numbers, and returns the group number and user number in DECIMAL
        /// </summary>
        /// <param name="uic">UIC string. Must be in NUMERIC form including brackets, e.g. [200,1]</param>
        /// <param name="decimalGroupNumber">Returned GROUP number in DECIMAL</param>
        /// <param name="decimalUserNumber">Returned USER number in DECIMAL</param>
        private static bool uicToDecimal(string uic, out uint decimalGroup, out uint decimalUser)
        {
            var uicParts = uic.Trim().Replace("[", "").Replace("]", "").Split(',');
            uint groupNumber, userNumber;

            if (uicParts.Length == 2 
                && uint.TryParse(uicParts[0], out groupNumber) && groupNumber >= 1 && groupNumber <= 37776
                && uint.TryParse(uicParts[1], out userNumber) && userNumber >= 1 && userNumber <= 177776)
            {
                decimalGroup = octalToDecimal(groupNumber);
                decimalUser = octalToDecimal(userNumber);
                return true;
            }
            decimalGroup = 0;
            decimalUser = 0;
            return false;
        }

        /// <summary>
        /// Convert an unsigned int OCTAL value to the equivalent unsigned DECIMAL value
        /// </summary>
        /// <param name="octalNumber">Octal number to convert</param>
        /// <returns>Decimal equivalent</returns>
        private static uint octalToDecimal(uint octalNumber)
        {
            uint octal = octalNumber, r, i = 0;
            double decnum = 0;

            while (octalNumber != 0)
            {
                r = octalNumber % 10;
                decnum = decnum + (r * Math.Pow(8, i++));
                octalNumber = octalNumber / 10;
            }

            return Convert.ToUInt32(decnum);
        }
    }
}