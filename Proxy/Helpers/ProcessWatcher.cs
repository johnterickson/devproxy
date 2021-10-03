using System;
using System.Management;
using WMI.Win32;

namespace DevProxy
{
    public class TrackedProcess
    {
        public TrackedProcess(Win32_Process process)
        {
            this.Id = process.ProcessId;
            this.ParentId = process.ParentProcessId;
            this.IsAuthRoot = false;
        }

        public readonly uint Id;
        public readonly uint ParentId;
        public bool IsAuthRoot;
    }

    // https://weblogs.asp.net/whaggard/438006

    public delegate void ProcessEventHandler(Win32_Process proc);

    public class ProcessWatcher : ManagementEventWatcher
    {
        // Process Events
        public event ProcessEventHandler ProcessCreated;
        public event ProcessEventHandler ProcessDeleted;
        public event ProcessEventHandler ProcessModified;

        // WMI WQL process query strings
        static readonly string WMI_OPER_EVENT_QUERY = @"SELECT * FROM 
__InstanceOperationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'";
        static readonly string WMI_OPER_EVENT_QUERY_WITH_PROC =
            WMI_OPER_EVENT_QUERY + " and TargetInstance.Name = '{0}'";

        public ProcessWatcher()
        {
            Init(string.Empty);
        }
        public ProcessWatcher(string processName)
        {
            Init(processName);
        }
        private void Init(string processName)
        {
            this.Query.QueryLanguage = "WQL";
            if (string.IsNullOrEmpty(processName))
            {
                this.Query.QueryString = WMI_OPER_EVENT_QUERY;
            }
            else
            {
                this.Query.QueryString =
                    string.Format(WMI_OPER_EVENT_QUERY_WITH_PROC, processName);
            }

            this.EventArrived += new EventArrivedEventHandler(watcher_EventArrived);
        }

        public void SeedExistingProcesses()
        {
            foreach (var p in new ManagementObjectSearcher("SELECT * FROM Win32_Process").Get())
            {
                HandleEvent(EventType.__InstanceCreationEvent, p);
            }
        }

        private void watcher_EventArrived(object sender, EventArrivedEventArgs e)
        {
            HandleEvent(
                Enum.Parse<EventType>(e.NewEvent.ClassPath.ClassName),
                e.NewEvent["TargetInstance"] as ManagementBaseObject);
        }

        enum EventType
        {
            __InstanceCreationEvent,
            __InstanceDeletionEvent,
            __InstanceModificationEvent
        }

        private void HandleEvent(EventType eventType, ManagementBaseObject eventObj)
        {
            Win32_Process proc = new Win32_Process(eventObj);

            switch (eventType)
            {
                case EventType.__InstanceCreationEvent:

                    if (ProcessCreated != null)
                    {
                        ProcessCreated(proc);
                    }
                    break;
                case EventType.__InstanceDeletionEvent:

                    if (ProcessDeleted != null)
                    {
                        ProcessDeleted(proc);
                    }
                    break;
                case EventType.__InstanceModificationEvent:
                    if (ProcessModified != null)
                    {
                        ProcessModified(proc);
                    }
                    break;
            }
        }
    }
}
