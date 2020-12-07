using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VMSBuildSync
{
    class Logger
    {
        ~Logger()
        {
            _writer.Flush();
            _writer.Dispose();
        }
        private StreamWriter _writer;
        private int _logLevel;
        private bool _console;
        private static Logger _instance;

        public static void Init(string logfileName, int level, bool console)
        {
            _instance = new Logger
            {
                _writer = string.IsNullOrWhiteSpace(logfileName) ? null : new StreamWriter(File.Open(logfileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), leaveOpen: false),
                _logLevel = level,
                _console = console
            };
            if(_instance._writer != null)
                _instance._writer.AutoFlush = true;
        }
        public static void WriteLine(int level, string value)
        {
            var timeString = DateTime.UtcNow.ToShortTimeString();
            if (_instance != null)
            {
                if (level > _instance._logLevel)
                {
                    _instance._writer?.WriteLine(timeString + ": " + value);
                    if (_instance._console)
                        Console.WriteLine(timeString + ": " + value);
                }
            }
            else
            {
                Console.WriteLine("NO-LOGGER: " + timeString + ": " + value);
            }
        }
    }
}
