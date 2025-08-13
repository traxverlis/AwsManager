using System;
using System.Diagnostics;

namespace AwsManager.Models
{
    public class TrackedSessionModel
    {
        public int ProcessId { get; }
        public string Description { get; }
        public DateTime StartTime { get; }
        public Process Process { get; }

        public TrackedSessionModel(Process process, string description)
        {
            Process = process;
            try
            {
                ProcessId = process.Id;
                StartTime = process.StartTime;
            }
            catch (InvalidOperationException)
            {
                // Process may have exited already
                ProcessId = -1;
                StartTime = DateTime.Now;
            }
            Description = description;
        }
    }
}