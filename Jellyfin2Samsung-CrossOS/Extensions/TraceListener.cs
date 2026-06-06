using System;
using System.Diagnostics;
using System.IO;

namespace Apps2Samsung.Extensions
{
    public sealed class FileTraceListener : TraceListener
    {
        private readonly string _filePath;

        public FileTraceListener(string filePath)
        {
            _filePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        }

        public override void Write(string? message)
        {
            if (message == null) return;
            File.AppendAllText(_filePath, message);
        }

        public override void WriteLine(string? message)
        {
            if (message == null) return;
            File.AppendAllText(
                _filePath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }

}
