using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ErrorChecker.FileAccess
{
    public static class FileManager
    {
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        private const int DEFAULT_TIMEOUT_MS = 5000;

        public static async Task<byte[]> SafeReadAllBytesAsync(string filePath, CancellationToken token)
        {
            if (!await semaphore.WaitAsync(DEFAULT_TIMEOUT_MS, token))
            {
                throw new TimeoutException("Timeout lors de l'attente du verrou de fichier");
            }

            try
            {
                return await File.ReadAllBytesAsync(filePath, token);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public static async Task SafeWriteAllBytesAsync(string filePath, byte[] bytes, CancellationToken token)
        {
            if (!await semaphore.WaitAsync(DEFAULT_TIMEOUT_MS, token))
            {
                throw new TimeoutException("Timeout lors de l'attente du verrou de fichier");
            }

            try
            {
                await File.WriteAllBytesAsync(filePath, bytes, token);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public static async Task SafeDeleteAsync(string filePath, CancellationToken token)
        {
            if (!await semaphore.WaitAsync(DEFAULT_TIMEOUT_MS, token))
            {
                throw new TimeoutException("Timeout lors de l'attente du verrou de fichier");
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
