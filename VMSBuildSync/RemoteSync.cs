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
using System.Text.Json;
using System.Net.Sockets;

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
        TaskCompletionSource<bool> _tcs;
        private DirectoryZip _directoryZip;
        HashSet<string> _activeDirSync = new HashSet<string>();
        private static Regex newLineRegex = new Regex(@"\w*\$\s*$");
        public static int SSHTimeout = 480;
        bool exclFile = false;
        Exclusions excl;


        //  \w*\$\s*$
        //Any number of word (alphanumeric) characters


        //remote attribute mapper supplies Regex's in a similar manor to .gitattributes
        //if the regex matches we apply the value (int) as the ExternalFileSystem attribute inside the zip archive
        //this allows us to effectively control readonly, executable, filetype, permissions
        //the value (string) tells us the line endings, if its blank we leave the file alone
        //if the value is non blank and differs from the current platform we'll perform line ending translation
        //the size change caused by this line ending translation is taken into account when comparing remote and local file sizes
        public RemoteSync(string host, string username, string password, string localRootDirectory,
            string remoteRootDirectory, string searchPattern, List<Tuple<Regex, VMSFileConfig>> remoteAttributeMapper = null, string forceOverride = null, TaskCompletionSource<bool> tcs = null)
        {
            _forceOverride = false;
            if (forceOverride != null)
            {
                bool.TryParse(forceOverride, out _forceOverride);
            }
            _tcs = tcs;
            _host = host;
            _username = username;
            _password = password;
            _searchPattern = searchPattern;
            _localRootDirectory = localRootDirectory;
            _remoteRootDirectory = remoteRootDirectory;
            _directoryZip = new DirectoryZip() { FileAttributeList = remoteAttributeMapper };

            //Get the SSH connection connected
            tryConnect("SSH", _client = new SshClient(host, username, password), true);

            //Get a ShellStream that is used to send commands to and receive responses from the remote shell.
            _shellStream = _client.CreateShellStream("sych - unzip", 80, 120, 640, 480, short.MaxValue);
            //TODO: Why does this take 20 seconds to complete?
            _shellStream.Expect(newLineRegex, TimeSpan.FromSeconds(SSHTimeout));

            //Get the SFTP connection connected
            tryConnect("SFTP", _sftp = new SftpClient(host, username, password) { OperationTimeout = TimeSpan.FromSeconds(SSHTimeout) }, true);

            var tsk = InitialSync(_localRootDirectory, _remoteRootDirectory);

            tsk.ContinueWith((tmp) =>
            {
                Logger.WriteLine(10, "initial sync completed");
                _fsw = new FileSystemWatcher(localRootDirectory, searchPattern);
                _fsw.InternalBufferSize = 1024 * 64;
                _fsw.IncludeSubdirectories = true;
                _fsw.NotifyFilter = NotifyFilters.LastWrite;
                _fsw.Changed += Fsw_Changed;
                _fsw.Error += _fsw_Error;
                _fsw.EnableRaisingEvents = true;
            });

            //If we have an exclusions.json file, load it
            if (File.Exists("exclusions.json"))
            {
                try
                {
                    string readExclusions = File.ReadAllText(@"exclusions.json"); //both windows (alt) and linux use / as separator
                    excl = JsonSerializer.Deserialize<Exclusions>(readExclusions);
                    //Lower case all the file extensions
                    if (excl.ftypes.Count<string>() > 0)
                    {
                        for (int ix = 0; ix < excl.ftypes.Count<string>() - 1; ix++)
                        {
                            excl.ftypes[ix] = excl.ftypes[ix].ToLower();
                        }
                    }
                    Logger.WriteLine(10, $"file and folder exclusions loaded from exclusions.json");
                }
                catch
                {
                    Logger.WriteLine(10, $"WARNING: Failed to process exclusions.json!");
                }
            }
        }

        private void tryConnect(string service, BaseClient client, bool terminateProcessOnException = false)
        {
            try
            {
                client.Connect();
            }
            catch (Exception ex)
            {
                bool gracefulFail = false;

                if (ex is SocketException)
                {
                    Logger.WriteLine(10, $"ERROR: During { service } startup a Socket connection could not be established! Check your host name/address, and that the host is running.");
                    gracefulFail = true;
                }
                else if (ex is SshConnectionException)
                {
                    Logger.WriteLine(10, $"ERROR: During { service } startup an SSH session could not be established! Check SSH is enabed on the server.");
                    gracefulFail = true;
                }
                else if (ex is SshAuthenticationException)
                {
                    Logger.WriteLine(10, $"ERROR: During { service } startup SSH authentication failed. Check your username and password!");
                    gracefulFail = true;
                }
                else if (ex is ProxyException)
                {
                    Logger.WriteLine(10, $"ERROR: During { service } startup a proxy connection could not be established!");
                    gracefulFail = true;
                }
                else if (ex is SshOperationTimeoutException)
                {
                    Logger.WriteLine(10, $"SSH During { service } startup the connection timed out!");
                    gracefulFail = true;
                }

                if (gracefulFail)
                {
                    _tcs.TrySetResult(false);
                    if (terminateProcessOnException)
                    {
                        Environment.Exit(1);
                    }
                }

                throw;
            }
        }

        private void _fsw_Error(object sender, ErrorEventArgs e)
        {
            var exception = e.GetException();
            switch (exception)
            {
                case UnauthorizedAccessException uae:
                    if(!uae.Message.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
                        Logger.WriteLine(10, $"file system watcher errored with {exception.ToString()}");
                    break;
                default:
                    Logger.WriteLine(10, $"file system watcher errored with {exception.ToString()}");
                    break;
            }
            
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
                        exclFile = false;

                        //skip hidden files
                        if (localFile.StartsWith("."))
                            continue;

                        //Do we have exclusions
                        if (excl != null && excl.ftypes != null && excl.ftypes.Count<string>() > 0)
                        {
                            //skip excluded file types
                            foreach (var item in excl.ftypes)
                            {
                                if (localFile.ToLower().EndsWith(item))
                                {
                                    exclFile = true;
                                    break;
                                }
                            }
                            if (exclFile)
                                continue;
                        }

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
                                GetFileName,
                                file => (int)file.Value.Length, StringComparer.OrdinalIgnoreCase),
                            cleanDirectoryListing.ToDictionary(
                                GetFileName,
                                file => file.Value.LastWriteTime, StringComparer.OrdinalIgnoreCase),
                            _forceOverride);

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

        private static string GetFileName(KeyValuePair<string, SftpFile> file)
        {
            var remoteName = Path.GetFileName(file.Key);
            if (remoteName.EndsWith("."))
            {
                remoteName = remoteName.Substring(0, remoteName.Length - 1);
            }
            return remoteName;
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

                if (!_client.IsConnected)
                    _client.Connect();
                try
                {
                    _shellStream = _client.CreateShellStream("sync - unzip", 80, 120, 640, 480, short.MaxValue);
                }
                catch
                {
                    _client.Disconnect();
                    _client.Connect();
                    _shellStream = _client.CreateShellStream("sync - unzip", 80, 120, 640, 480, short.MaxValue);
                }
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
                _shellStream = _client.CreateShellStream("sync - unzip", 80, 120, 640, 480, short.MaxValue);
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
                    await CreateDirectory(_sftp, remotePath);
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

                        if (excl != null && excl.directories.Count<string>() > 0 && excl.directories.Contains(directoryName))
                            continue;

                        if (!remoteDirectories.ContainsKey(directoryName))
                        {
                            Logger.WriteLine(2, $"Directory {localPath + Path.DirectorySeparatorChar + directoryName} is being created at {remotePath + " / " + directoryName}");
                            try
                            {
                                await CreateDirectory(_sftp, remotePath + "/" + directoryName);
                            }
                            catch (SshException)
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

        private static async Task<T> SSHRetryReset<T>(SftpClient sftp, Func<Task<T>> action)
        {
            int retryCount = 0;
            var retrying = true;
            while (retrying && (retryCount++ < 100))
            {
                try
                {
                    return await action();
                }
                catch (InvalidOperationException)
                {
                    //reset connection and retry
                    if (sftp.IsConnected)
                    {
                        sftp.Disconnect();
                    }
                    sftp.Connect();
                    retrying = true;
                }
                catch (Renci.SshNet.Common.SshOperationTimeoutException)
                {
                    sftp.Disconnect();
                    sftp.Connect();
                    retrying = true;
                }
                catch (Renci.SshNet.Common.SshException ex)
                {
                    if (ex.Message.Contains("channel was closed", StringComparison.OrdinalIgnoreCase))
                    {
                        sftp.Disconnect();
                        sftp.Connect();
                        retrying = true;
                    }
                    else if (ex.Message.Contains("Client not connected", StringComparison.OrdinalIgnoreCase))
                    {
                        //reset connection and retry
                        if (sftp.IsConnected)
                        {
                            sftp.Disconnect();
                        }
                        sftp.Connect();
                        retrying = true;
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            throw new InvalidOperationException("attempted to retry operation 100 times, continued to fail");
        }

        public static async Task CreateDirectory(SftpClient sftp, string directoryPath)
        {
            await SSHRetryReset(sftp, () =>
            {
                sftp.CreateDirectory(directoryPath);
                return Task.FromResult(true);
            });
        }

        public static async Task UploadFileAsync(SftpClient sftp, Stream file, string destination)
        {
            Func<Stream, string, AsyncCallback, object, IAsyncResult> begin = (stream, path, callback, state) => sftp.BeginUploadFile(stream, path, true, callback, state, null);

            await SSHRetryReset(sftp, async () =>
            {
                await Task.Factory.FromAsync(begin, sftp.EndUploadFile, file, destination, null);
                return true;
            });
        }

        public static async Task<IEnumerable<SftpFile>> ListDirectoryAsync(SftpClient sftp, string path)
        {
            Func<string, AsyncCallback, object, IAsyncResult> begin = (bpath, callback, state) => sftp.BeginListDirectory(bpath, callback, state, null);
            return await SSHRetryReset(sftp, async () =>
            {
                return await Task.Factory.FromAsync(begin, sftp.EndListDirectory, path, null);
            });
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
            try
            {
                if (!arg.FullPath.Contains(".git"))
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

                        try
                        {
                            for (int i = 0; i < 100 && !IsFileReady(arg.FullPath); i++)
                                Thread.Sleep(50);

                            if (!IsFileReady(arg.FullPath))
                            {
                                Logger.WriteLine(10, $"synchronizing {arg.FullPath} failed with because it didnt exist on disk (deleted before fsw got to it?)");
                                return;
                            }

                            var relativePath = _localRootDirectory == changedPath ? "" : changedPath.Substring(_localRootDirectory.Length).Replace('\\', '/');
                            var fullRemotePath = _remoteRootDirectory + relativePath;

                            //check if we're a new directory
                            if (Directory.Exists(arg.FullPath))
                            {
                                await InitialSync(arg.FullPath, fullRemotePath);
                            }
                            else
                            {
                                var fileInfos = (await SyncDirectoryAsync(changedPath, fullRemotePath, _searchPattern));
                                if (!fileInfos.Any(fileInfo => string.Compare(fileInfo.Name, Path.GetFileName(arg.FullPath), true) == 0))
                                    Logger.WriteLine(10, $"synchronizing failed for {arg.FullPath}, sync was requested but the remote system had a newer version or something went wrong");
                                else
                                {
                                    foreach (var fileInfo in fileInfos)
                                    {
                                        Logger.WriteLine(8, "synchronizing " + Path.Combine(Path.GetDirectoryName(arg.FullPath), fileInfo.Name));
                                    }
                                }
                            }
                        }
                        finally
                        {
                            lock (_activeDirSync)
                            {
                                _activeDirSync.Remove(changedPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine(10, $"synchronizing {arg.FullPath} failed with {ex}");
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
