using System.Runtime.InteropServices;

namespace nSpectrum.Capture;

/// <summary>Joins the calling thread to the MMCSS "Pro Audio" task so the DSP worker is scheduled like audio.</summary>
public static partial class Mmcss
{
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    /// <summary>Returns false when MMCSS is unavailable; the thread keeps its normal priority.</summary>
    public static bool JoinProAudio()
    {
        try
        {
            uint index = 0;
            return AvSetMmThreadCharacteristics("Pro Audio", ref index) != 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
