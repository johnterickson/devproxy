using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using WMI.Win32;

namespace DevProxy
{
    public class ProcessTracker
    {
        private readonly ProcessWatcher _processWatcher = new ProcessWatcher();
        private readonly ConcurrentDictionary<uint, TrackedProcess> _processes = new ConcurrentDictionary<uint, TrackedProcess>();

        public ProcessTracker()
        {
            _processWatcher.ProcessCreated += ProcessCreated;
            _processWatcher.ProcessModified += ProcessModified;
            _processWatcher.ProcessDeleted += ProcessDeleted;

            _processWatcher.Start();
            _processWatcher.SeedExistingProcesses();
        }

        public bool TrySetAuthRoot(uint processId)
        {
            TrackedProcess process;
            if (_processes.TryGetValue(processId, out process))
            {
                process.IsAuthRoot = true;
                return true;
            }
            else
            {
                return false;
            }
        }

        public async Task<(bool, uint)> TryGetAuthRootAsync(uint processId)
        {
            while (true)
            {
                TrackedProcess process;
                if (!_processes.TryGetValue(processId, out process))
                {
                    var timer = Stopwatch.StartNew();
                    while (!_processes.TryGetValue(processId, out process))
                    {
                        if (timer.Elapsed.TotalSeconds > 10)
                        {
                            break;
                        }
                        await Task.Delay(100);
                    }
                }

                if (process == null)
                {
                    break;
                }

                if (process.IsAuthRoot)
                {
                    return (true, process.Id);
                }
                processId = process.ParentId;
            }

            return (false, uint.MaxValue);
        }

        private void ProcessCreated(Win32_Process proc)
        {
            if (!_processes.TryAdd(proc.ProcessId, new TrackedProcess(proc)))
            {
                // TODO: this happens sometimes at startup
                //throw new Exception("Failed to add process to tracker");
            }
        }

        private void ProcessModified(Win32_Process proc)
        {
            // do nothing
        }


        private void ProcessDeleted(Win32_Process proc)
        {
            _processes.TryRemove(proc.ProcessId, out var _dummy);
        }
    }
}
