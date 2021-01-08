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
            if (args.Length < 9 )
            {
                Console.WriteLine(
                    "usage: RemoteSFTPSync.exe host username password localRootDir remoteRootDir searchPattern forceupdate, vmsUserNumber, vmsGroupNumber, [logfile, loglevel]");
            }
            else
            {
                if(args.Length > 9)
                {
                    //log everything
                    int logLevel = -1;
                    if (args.Length > 10)
                        int.TryParse(args[10], out logLevel);

                    Logger.Init(args[9], logLevel, Debugger.IsAttached || logLevel == -1);
                }

                var regexs = new List<Tuple<Regex, VMSFileConfig>>();
                //jeff uic is 483 : 40
                if (!uint.TryParse(args[7], out var uicUser))
                    Logger.WriteLine(10, $"failed to parse vmsUserNumber, this should be a decimal number (vms system utilities report this in octal)");

                if (!uint.TryParse(args[8], out var uicGroup))
                    Logger.WriteLine(10, $"failed to parse vmsGroupNumber, this should be a decimal number (vms system utilities report this in octal)");


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
                    Console.WriteLine("Press enter to exit remote sftp sync");
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
}