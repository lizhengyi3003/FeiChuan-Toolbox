using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TunHotspot;

internal static class FontCatalog
{
    internal static List<string> Installed()
    {
        var iid = typeof(IDWriteFactory).GUID;
        Marshal.ThrowExceptionForHR(DWriteCreateFactory(0, ref iid, out var factory));
        try
        {
            Marshal.ThrowExceptionForHR(factory.GetSystemFontCollection(out var fonts, true));
            try
            {
                var result = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                for (uint i = 0; i < fonts.GetFontFamilyCount(); i++)
                {
                    Marshal.ThrowExceptionForHR(fonts.GetFontFamily(i, out var family));
                    try
                    {
                        Marshal.ThrowExceptionForHR(family.GetFamilyNames(out var names));
                        try
                        {
                            Marshal.ThrowExceptionForHR(names.FindLocaleName(CultureInfo.CurrentUICulture.Name, out uint index, out bool exists));
                            if (!exists) { names.FindLocaleName("en-us", out index, out exists); if (!exists) index = 0; }
                            names.GetStringLength(index, out uint count); var text = new StringBuilder((int)count + 1);
                            Marshal.ThrowExceptionForHR(names.GetString(index, text, count + 1)); result.Add(text.ToString());
                            if(names.FindLocaleName("en-us",out uint english,out bool hasEnglish)>=0&&hasEnglish&&english!=index)
                            {names.GetStringLength(english,out uint length);var alias=new StringBuilder((int)length+1);if(names.GetString(english,alias,length+1)>=0)result.Add(alias.ToString());}
                        }
                        finally { Marshal.FinalReleaseComObject(names); }
                    }
                    finally { Marshal.FinalReleaseComObject(family); }
                }
                return result.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            finally { Marshal.FinalReleaseComObject(fonts); }
        }
        finally { Marshal.FinalReleaseComObject(factory); }
    }
    [DllImport("dwrite.dll", ExactSpelling = true)] private static extern int DWriteCreateFactory(uint type, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IDWriteFactory factory);
    [ComImport,Guid("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory { [PreserveSig] int GetSystemFontCollection(out IDWriteFontCollection collection, [MarshalAs(UnmanagedType.Bool)] bool update); }
    [ComImport,Guid("A84CEE02-3EEA-4EEE-A827-87C1A02A0FCC"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig] uint GetFontFamilyCount(); [PreserveSig] int GetFontFamily(uint index, out IDWriteFontFamily family);
    }
    [ComImport,Guid("DA20D8EF-812A-4C43-9802-62EC4ABD7ADD"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        [PreserveSig] int GetFontCollection(out nint collection); [PreserveSig] uint GetFontCount(); [PreserveSig] int GetFont(uint index, out nint font);
        [PreserveSig] int GetFamilyNames(out IDWriteLocalizedStrings names);
    }
    [ComImport,Guid("08256209-099A-4B34-B86D-C22B110E7771"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteLocalizedStrings
    {
        [PreserveSig] uint GetCount();
        [PreserveSig] int FindLocaleName([MarshalAs(UnmanagedType.LPWStr)] string locale, out uint index, [MarshalAs(UnmanagedType.Bool)] out bool exists);
        [PreserveSig] int GetLocaleNameLength(uint index, out uint length);
        [PreserveSig] int GetLocaleName(uint index, [Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, uint size);
        [PreserveSig] int GetStringLength(uint index, out uint length);
        [PreserveSig] int GetString(uint index, [Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, uint size);
    }
}
