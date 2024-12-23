using System;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;

namespace ErrorChecker.Logging
{
    public class Logger
    {
        private readonly string logFilePath;
        private readonly ConcurrentQueue<string> logQueue;
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly int flushInterval = 1000; // ms

        public LogLevel MinimumLevel { get; }

        public Logger(string basePath, LogLevel MinimumLevel)
        {
            string logDirectory = Path.Combine(basePath, "logs");
            Directory.CreateDirectory(logDirectory);
            
            logFilePath = Path.Combine(logDirectory, 
                $"errorchecker_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            
            logQueue = new ConcurrentQueue<string>();
            cancellationTokenSource = new CancellationTokenSource();

            // Démarrer le thread de flush des logs
            Task.Run(() => FlushLogsAsync(cancellationTokenSource.Token));
            this.MinimumLevel = MinimumLevel;
        }

        public void Log(LogLevel level, string message)
        {
            if (level < MinimumLevel)
                return;

            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            logQueue.Enqueue(logMessage);
            
            // Afficher aussi dans la console pour le débogage
            Console.WriteLine(logMessage);
        }

        public void LogInfo(string message) => Log(LogLevel.Info, message);
        public void LogWarning(string message) => Log(LogLevel.Warning, message);
        public void LogError(string message) => Log(LogLevel.Error, message);
        public void LogDebug(string message) => Log(LogLevel.Debug, message);

        private async Task FlushLogsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!logQueue.IsEmpty)
                    {
                        var logs = new List<string>();
                        while (logQueue.TryDequeue(out string log))
                        {
                            logs.Add(log);
                        }

                        if (logs.Count > 0)
                        {
                            await File.AppendAllLinesAsync(logFilePath, logs, token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erreur lors de l'écriture des logs : {ex.Message}");
                }

                await Task.Delay(flushInterval, token);
            }
        }

        public void Dispose()
        {
            cancellationTokenSource.Cancel();
            // Flush final des logs
            if (!logQueue.IsEmpty)
            {
                var remainingLogs = new List<string>();
                while (logQueue.TryDequeue(out string log))
                {
                    remainingLogs.Add(log);
                }
                File.AppendAllLines(logFilePath, remainingLogs);
            }
        }
    }

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }
}
