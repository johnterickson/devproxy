using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevProxy
{
    class IpcMessage
    {
        public string Command { get; set; }
        public Dictionary<string, string> Args { get; set; }
    }

    sealed class Ipc : IDisposable
    {
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

        private readonly string _pipeName;
        public readonly Task Completion;
        private readonly Func<object, string, CancellationToken, Task<string>> _handler;

        public Ipc(string pipeName, Func<object, string, CancellationToken, Task<string>> handler)
        {
            _pipeName = pipeName;
            _handler = handler;
            Completion = Task.Run(() => RunServerAsync(_shutdown.Token));
        }

        public static async Task SendMessageAsync(PipeStream stream, string message, CancellationToken cancellationToken)
        {
            byte[] messageBuffer = Encoding.UTF8.GetBytes(message);
            byte[] lengthBuffer = BitConverter.GetBytes(messageBuffer.Length);
            // Console.WriteLine($"Sending {lengthBuffer.Length} bytes = {BitConverter.ToInt32(lengthBuffer)}");
            await stream.WriteAsync(lengthBuffer, 0, lengthBuffer.Length, cancellationToken);
            // Console.WriteLine($"Sending {messageBuffer.Length} bytes = `{message}`");
            await stream.WriteAsync(messageBuffer, 0, messageBuffer.Length, cancellationToken);
        }

        public static async Task<string?> ReceiveMessageAsync(PipeStream stream, CancellationToken cancellationToken)
        {
            var lengthBuffer = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int bytesRead = await stream.ReadAsync(lengthBuffer, i, 1, cancellationToken);
                if (bytesRead == 0)
                {
                    return null;
                }
            }
            int bytesToRead = BitConverter.ToInt32(lengthBuffer, 0);

            var messageBuffer = new byte[bytesToRead];
            while (bytesToRead > 0)
            {
                int bytesRead = await stream.ReadAsync(messageBuffer, messageBuffer.Length - bytesToRead, bytesToRead, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }
                bytesToRead -= bytesRead;
            }
            if (bytesToRead != 0)
            {
                throw new EndOfStreamException();
            }

            return Encoding.UTF8.GetString(messageBuffer);
        }

        public static async Task<string> SendAsync(string pipeName, string message, CancellationToken cancellationToken)
        {
            using (var ipcPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous))
            {
                await ipcPipe.ConnectAsync(cancellationToken);
                await SendMessageAsync(ipcPipe, message, cancellationToken);
                return await ReceiveMessageAsync(ipcPipe, cancellationToken);
            }
        }

        private async Task RunServerAsync(CancellationToken cancellationToken)
        {
            Task nextListener = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (var ipcPipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, -1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous))
                    {
                        await ipcPipe.WaitForConnectionAsync(cancellationToken);
                        nextListener = Task.Run(() => RunServerAsync(cancellationToken));

                        string message;
                        while (null != (message = await ReceiveMessageAsync(ipcPipe, cancellationToken)))
                        {
                            string response = await _handler(ipcPipe, message, cancellationToken);
                            await SendMessageAsync(ipcPipe, response, cancellationToken);
                        }
                    }
                }
                catch (TaskCanceledException e) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }

            if (nextListener != null)
            {
                await nextListener;
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try
            {
                Completion.Wait();
            }
            catch
            {

            }
        }
    }
}
