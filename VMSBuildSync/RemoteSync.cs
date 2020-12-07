﻿using Renci.SshNet;
using Renci.SshNet.Sftp;
using System;
using System.Collections.Generic;
using System.IO;
 using System.IO.Compression;
 using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
 using System.Text.RegularExpressions;
 using System.Threading;
using System.Threading.Tasks;
 using Renci.SshNet.Common;
using System.Diagnostics;

namespace VMSBuildSync
{
    class RemoteSync : IDisposable
    {
        string _host;
        string _username;
        string _password;
        string _searchPattern;
        string _localRootDirectory;
        string _remoteRootDirectory;
        bool _forceOverride;
        SftpClient _sftp;
        ShellStream _shellStream;
        SshClient _client;
        FileSystemWatcher _fsw;
        private DirectoryZip _directoryZip;
        HashSet<string> _activeDirSync = new HashSet<string>();
        private static Regex newLineRegex = new Regex(@"\w*\$\s*$");
        public static int SSHTimeout = 480;
        //remote attribute mapper supplies Regex's in a similar manor to .gitattributes
        //if the regex matches we apply the value (int) as the ExternalFileSystem attribute inside the zip archive
        //this allows us to effectively control readonly, executable, filetype, permissions
        //the value (string) tells us the line endings, if its blank we leave the file alone
        //if the value is non blank and differs from the current platform we'll perform line ending translation
        //the size change caused by this line ending translation is taken into account when comparing remote and local file sizes
        public RemoteSync(string host, string username, string password, string localRootDirectory,
            string remoteRootDirectory, string searchPattern, List<Tuple<Regex, VMSFileConfig>> remoteAttributeMapper = null, string forceOverride = null)
        {
            _forceOverride = false;
            if (forceOverride != null)
            {
                bool.TryParse(forceOverride, out _forceOverride);
            }
            _host = host;
            _username = username;
            _password = password;
            _searchPattern = searchPattern;
            _localRootDirectory = localRootDirectory;
            _remoteRootDirectory = remoteRootDirectory;
            _directoryZip = new DirectoryZip() { FileAttributeList = remoteAttributeMapper };
            _client = new SshClient(host, username, password);
            _client.Connect();
            _shellStream = _client.CreateShellStream("sych - unzip", 80, 120, 640, 480, short.MaxValue);
            _shellStream.Expect(newLineRegex, TimeSpan.FromSeconds(SSHTimeout));
            _sftp = new SftpClient(host, username, password);
            _sftp.Connect();
            var tsk = InitialSync(_localRootDirectory, _remoteRootDirectory);

            tsk.ContinueWith((tmp) =>
            {
                Logger.WriteLine(10, "initial sync completed");
                _fsw = new FileSystemWatcher(localRootDirectory, searchPattern);
                _fsw.IncludeSubdirectories = true;
                _fsw.NotifyFilter = NotifyFilters.LastWrite;
                _fsw.Changed += Fsw_Changed;
                _fsw.EnableRaisingEvents = true;
            });
        }

        
        public async Task<IEnumerable<FileInfo>> SyncDirectoryAsync(string sourcePath, string destinationPath, string searchPattern)
        {
            var synchedFiles = new List<FileInfo>();
            try
            {
                //get list of current files
                //zip difference
                //send zip
                //use ssh to unzip on remote system

                var directoryListing = await ListDirectoryAsync(_sftp, destinationPath);

                var cleanDirectoryListing = new Dictionary<string, SftpFile>();
                foreach (var remoteFile in directoryListing)
                {
                    var cleanName = StripFileRevision(remoteFile.FullName);
                    if (cleanDirectoryListing.TryGetValue(cleanName, out var dup))
                    {
                        if (GetFileRevision(dup.Name) > GetFileRevision(remoteFile.Name))
                        {
                            cleanDirectoryListing[cleanName] = remoteFile;
                        }
                    }
                    else
                    {
                        cleanDirectoryListing.Add(cleanName, remoteFile);
                    }
                }

                Logger.WriteLine(1, $"Searching for files in {sourcePath}");

                var sourceListing = Directory.EnumerateFiles(sourcePath, searchPattern, SearchOption.TopDirectoryOnly);
                var fileStreamList = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
                string tempFileName = string.Empty;
                try
                {
                    foreach (var localFile in sourceListing)
                    {
                        //skip hidden files
                        if (localFile.StartsWith("."))
                            continue;

                        Logger.WriteLine(1, localFile);

                        var localStream = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 0x1000, useAsync: false);

                        fileStreamList.Add(localFile.Replace(_localRootDirectory, ""), localStream);
                    }

                    var zipList = new List<FileStream>(fileStreamList.Values);


                    if (zipList.Count > 0)
                    {
                        tempFileName = Path.GetFullPath(Path.Combine(_localRootDirectory, "../", Process.GetCurrentProcess().Id.ToString() + "tmpzip.zip"));
                        _directoryZip.MakeZipForDirectory(tempFileName, zipList,
                            cleanDirectoryListing.ToDictionary(
                                file =>
                                {
                                    var remoteName = Path.GetFileName(file.Key);
                                    if (remoteName.EndsWith("."))
                                    {
                                        remoteName = remoteName.Substring(0, remoteName.Length - 1);
                                    }
                                    return remoteName;
                                },
                                file => (int)file.Value.Length, StringComparer.OrdinalIgnoreCase), _forceOverride);

                        using var madeZip = ZipFile.Open(tempFileName, ZipArchiveMode.Read);

                        foreach (var zipEntry in madeZip.Entries)
                        {
                            synchedFiles.Add(new FileInfo(zipEntry.FullName));
                        }
                    }
                }
                finally
                {
                    foreach (var fs in fileStreamList)
                        fs.Value.Dispose();
                }

                if (synchedFiles.Count > 0)
                {
                    using (var archiveStream = File.OpenRead(tempFileName))
                    {
                        await _directoryZip.UnpackRemoteZip(SSHWriteLine, SSHExpect, _sftp, archiveStream, destinationPath,
                            _remoteRootDirectory);
                    }
                    if (_forceOverride && _localRootDirectory != sourcePath)
                    {
                        var vmsPath = _directoryZip.VMSifyPath(destinationPath);
                        await SSHWriteLine($"purge {vmsPath}");
                        await SSHExpect(newLineRegex);
                    }
                    //clean up after ourselves if we were successful
                    //leave the zip around otherwise so we have something to debug
                    //windows is supposed to clean the temp folder eventually anyway
                    File.Delete(tempFileName);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine(10, $"syncing files from {sourcePath} -> {destinationPath} failed with {ex}");
            }
            return synchedFiles;
        }

        public async Task SSHWriteLine(string text)
        {
            try
            {
                _shellStream.WriteLine(text);
            }
            catch (Exception e)
            {
                Logger.WriteLine(10, e.ToString());
                try
                {
                    _shellStream.Dispose();
                }
                catch
                {
                }
                _shellStream = _client.CreateShellStream("sych - unzip", 80, 120, 640, 480, short.MaxValue);
                _shellStream.Expect(newLineRegex, TimeSpan.FromSeconds(SSHTimeout));
                _shellStream.WriteLine(text);
            }
        }

        public async Task<string> SSHExpect(Regex match)
        {
            try
            {
                return _shellStream.Expect(match, TimeSpan.FromSeconds(SSHTimeout));
            }
            catch (Exception e)
            {
                Logger.WriteLine(10, e.ToString());
                try
                {
                    _shellStream.Dispose();
                }
                catch
                {
                }
                _shellStream = _client.CreateShellStream("sych - unzip", 80, 120, 640, 480, short.MaxValue);
                _shellStream.Expect(newLineRegex, TimeSpan.FromSeconds(SSHTimeout));
            }

            return string.Empty;
        }

        string StripFileRevision(string filename)
        {
            var semicolonPos = filename.LastIndexOf(';');
            if (semicolonPos != -1)
                return filename.Remove(semicolonPos);
            else
                return filename;
        }

        int GetFileRevision(string filename)
        {
            var semicolonPos = filename.LastIndexOf(';');
            if (semicolonPos != -1 && semicolonPos < filename.Length - 2)
            {
                var revisionString = filename.Substring(semicolonPos + 1);
                int revision;
                if (Int32.TryParse(revisionString, out revision))
                    return revision;
            }
            return -1;
        }
        
        public async Task InitialSync(string localPath, string remotePath)
        {
            Logger.WriteLine(1, $"initial sync for {localPath} -> {remotePath}");
            var localDirectories = Directory.GetDirectories(localPath);
            var remoteDirectories = new Dictionary<string, SftpFile>();
            try
            {
                if (_sftp.Exists(remotePath))
                {
                    var remoteItems = (await ListDirectoryAsync(_sftp, remotePath)).ToList();
                    remoteDirectories = remoteItems.Where(item => item.IsDirectory)
                        .ToDictionary(item => item.Name.Remove(item.Name.IndexOf(".DIR")));
                }
                else
                {
                    Logger.WriteLine(2, $"Directory {localPath} is being created at {remotePath}");
                    _sftp.CreateDirectory(remotePath);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine(10, $"{localPath} -> {remotePath} failed with {ex}");
            }
            foreach (var item in localDirectories)
            {
                try
                {
                    var directoryName = item.Split(Path.DirectorySeparatorChar).Last();
                    if (!directoryName.Contains("."))
                    {
                        if (!remoteDirectories.ContainsKey(directoryName))
                        {
                            Logger.WriteLine(2, $"Directory {localPath + Path.DirectorySeparatorChar + directoryName} is being created at {remotePath + " / " + directoryName}");
                            try
                            {
                                _sftp.CreateDirectory(remotePath + "/" + directoryName);
                            }
                            catch (SshException ex)
                            {
                                Logger.WriteLine(10, $"Directory {item} already exists but was reported missing");
                            }
                        }

                        await InitialSync(localPath + Path.DirectorySeparatorChar + directoryName, remotePath + "/" + directoryName);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(10, $"Directory {item} failed with {ex}");
                }
            }

            Logger.WriteLine(1, $"Directory {localPath} finished processing subfolders");

            await SyncDirectoryAsync( localPath, remotePath, _searchPattern);
        }

        public static async Task UploadFileAsync(SftpClient sftp, Stream file, string destination)
        {
            Func<Stream, string, AsyncCallback, object, IAsyncResult> begin = (stream, path, callback, state) => sftp.BeginUploadFile(stream, path, true, callback, state, null);
            await Task.Factory.FromAsync(begin, sftp.EndUploadFile, file, destination, null);
        }

        public static Task<IEnumerable<SftpFile>> ListDirectoryAsync(SftpClient sftp, string path)
        {
            Func<string, AsyncCallback, object, IAsyncResult> begin = (bpath, callback, state) => sftp.BeginListDirectory(bpath, callback, state, null);
            return Task.Factory.FromAsync(begin, sftp.EndListDirectory, path, null);
        }


        public static bool IsFileReady(String sFilename)
        {
            // If the file can be opened for exclusive access it means that the file
            // is no longer locked by another process.
            try
            {
                using (FileStream inputStream = File.Open(sFilename, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (inputStream.Length > 0)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }

                }
            }
            catch (Exception)
            {
                return false;
            }
        }

       

        private async void Fsw_Changed(object sender, FileSystemEventArgs arg)
        {
            if (arg.ChangeType == WatcherChangeTypes.Changed || arg.ChangeType == WatcherChangeTypes.Created)
            {
                var changedPath = Path.GetDirectoryName(arg.FullPath);
                lock (_activeDirSync)
                {
                    if (_activeDirSync.Contains(changedPath))
                        return;
                    else
                        _activeDirSync.Add(changedPath);
                }
                while (!IsFileReady(arg.FullPath))
                    Thread.Sleep(50);

                var relativePath = _localRootDirectory == changedPath ? "" : changedPath.Substring(_localRootDirectory.Length).Replace('\\', '/');
                var fullRemotePath = _remoteRootDirectory + relativePath;

                //check if we're a new directory
                if (Directory.Exists(arg.FullPath))
                {
                    _sftp.CreateDirectory(fullRemotePath);
                }

                foreach (var fileInfo in (await SyncDirectoryAsync(changedPath, fullRemotePath, _searchPattern)))
                {
                    Logger.WriteLine(8, "synchronizing " + fileInfo.FullName);
                }

                lock (_activeDirSync)
                {
                    _activeDirSync.Remove(changedPath);
                }
            }
        }

        public void Dispose()
        {
            _shellStream?.Close();
            _shellStream = null;
            
            _client?.Dispose();
            _client = null;
            
            _sftp?.Dispose();
            _sftp = null;

            _fsw?.Dispose();
            _fsw = null;
        }
    }
}
