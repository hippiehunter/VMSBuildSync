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
            try
            {
                _instance = new Logger
                {
                    _writer = string.IsNullOrWhiteSpace(logfileName) ? null : new StreamWriter(File.Open(logfileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), leaveOpen: false),
                    _logLevel = level,
                    _console = console
                };
                if (_instance._writer != null)
                    _instance._writer.AutoFlush = true;
            }
            catch (Exception)
            {
                Console.WriteLine($"{DateTime.Now.ToString()}: ERROR: Failed to initialize logging. Check your log file specification!");
                Environment.Exit(1);
            }
        }

        public static void WriteLine(int level, string value)
        {
            var timeString = DateTime.Now.ToString();
            if (_instance != null)
            {
                if (level > _instance._logLevel)
                {
                    try
                    {
                        _instance._writer?.WriteLine(timeString + ": " + value);
                    }
                    catch
                    {
                        Console.WriteLine(timeString + ": failed while logging");
                    }

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
