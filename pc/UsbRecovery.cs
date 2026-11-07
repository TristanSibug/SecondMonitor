using System;

// Retry a failed tunnel/app launch even if adb keeps reporting the same device.
sealed class UsbRecovery
{
    string serial;
    long nextAttempt;
    int failures;
    public bool ShouldAttempt(string device, bool connected, long nowMs)
    {
        if (device != serial)
        {
            serial = device; nextAttempt = 0; failures = 0;
        }
        return device != null && !connected && nowMs >= nextAttempt;
    }
    public void AttemptFinished(bool success, long nowMs)
    {
        failures = success ? 0 : Math.Min(failures + 1, 5);
        nextAttempt = nowMs + (success ? 10000 : Math.Min(30000, 3000 << (failures - 1)));
    }
}
