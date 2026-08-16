using System;
using System.Collections.Generic;
using ZenStates.Core;
using ZenStates.Core.Drivers;
using ZenTimings.Common;

namespace ZenTimings.Plugin
{
    public class SVI2Plugin : IPlugin
    {
        // Spent per call, not per plugin: as a field it stayed spent, and one run of contended
        // polls left the sensors dead for the rest of the session.
        private const int RetryLimit = 20;
        private const string VERSION = "1.1";

        public string Name => "SVI2 Sensors";

        public string Description => "";

        public string Author => "";

        public string Version => VERSION;

        public List<Sensor> Sensors { get; private set; }

        private Cpu cpuInstance;

        public SVI2Plugin(Cpu cpu)
        {
            cpuInstance = cpu;
            InitializeSensors();
        }

        private void InitializeSensors()
        {
            if (cpuInstance != null && cpuInstance.Status == IODriver.LibStatus.OK)
            {
                Sensors = new List<Sensor>
                {
                    new Sensor("VSOC", 0),
                    new Sensor("VCORE", 1),
                };
            }
        }

        public bool Update()
        {
            if (Sensors?.Count > 0 && cpuInstance != null)
            {
                uint socPlaneValue;
                uint vcorePlaneValue;
                int attempts = RetryLimit;
                do
                {
                    // VID 0 is 1.55 V, so a plane value of 0 passes for a real sample here; only
                    // the read itself can report that it never happened.
                    if (!ReadSensorValues(out socPlaneValue, out vcorePlaneValue))
                        return false;
                } while (Busy(socPlaneValue, vcorePlaneValue) && --attempts > 0);

                // Both planes or neither: the two are read a moment apart, so one still busy is
                // the ordinary case, and the block below publishes both.
                if (!Busy(socPlaneValue, vcorePlaneValue))
                {
                    UpdateSensorValue(socPlaneValue, Sensors[0]);
                    UpdateSensorValue(vcorePlaneValue, Sensors[1]);

                    return true;
                }
            }

            return false;
        }

        /// <summary>A plane still carrying the SMU's busy bits is not a sample yet.</summary>
        private static bool Busy(uint socPlaneValue, uint vcorePlaneValue)
        {
            return (socPlaneValue & 0xFF00) != 0 || (vcorePlaneValue & 0xFF00) != 0;
        }

        private bool ReadSensorValues(out uint socPlaneValue, out uint vcorePlaneValue)
        {
            socPlaneValue = 0;
            vcorePlaneValue = 0;

            // A neighbour monitoring tool can hold Global\Access_PCI for longer than this, and a
            // sample that was never taken must stay missing rather than reach the panel as VID 0.
            if (!Mutexes.WaitPciBus(10))
                return false;

            // NoLock because the mutex above is already held, and the Ex overloads because the
            // plain ones hand back a zero on a failed SMN read - the one value that turns into a
            // plausible 1.55 V rail. Both are read either way, so the release below stays paired.
            bool read = cpuInstance.ReadDwordExNoLock(cpuInstance.info.svi2.socAddress, ref socPlaneValue)
                & cpuInstance.ReadDwordExNoLock(cpuInstance.info.svi2.coreAddress, ref vcorePlaneValue);

            Mutexes.ReleasePciBus();

            return read;
        }

        private void UpdateSensorValue(uint planeValue, Sensor sensor)
        {
            uint vid = (planeValue >> 16) & 0xFF;
            sensor.Value = Convert.ToSingle(Utils.VidToVoltage(vid));
        }

        public void Open()
        {
            throw new NotImplementedException();
        }

        public void Close()
        {
            cpuInstance = null;
            Sensors = null;
        }
    }
}
