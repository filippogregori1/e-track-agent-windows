using System.Runtime.InteropServices;

namespace ActivityTracker.Windows.Native;

// UI Automation (UIAutomationCore.dll, parte di Windows) con le sole voci che servono a leggere la barra degli
// indirizzi di un browser. Le interfacce COM sono dichiarate a mano, nell'ordine esatto della vtable di
// UIAutomationClient.h: i metodi non usati restano come segnaposto, perché conta la posizione, non il nome.

internal static class Uia
{
    public static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    public const int TreeScope_Descendants = 0x4;
    public const int UIA_ControlTypePropertyId = 30003;
    public const int UIA_AutomationIdPropertyId = 30011;
    public const int UIA_ValueValuePropertyId = 30045;
    public const int UIA_EditControlTypeId = 50004;

    public static IUIAutomation Create()
    {
        var type = Type.GetTypeFromCLSID(CLSID_CUIAutomation, throwOnError: true)!;
        return (IUIAutomation)Activator.CreateInstance(type)!;
    }
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void _CompareElements();
    void _CompareRuntimeIds();
    void _GetRootElement();
    [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement? element);
    void _ElementFromPoint();
    void _GetFocusedElement();
    void _GetRootElementBuildCache();
    void _ElementFromHandleBuildCache();
    void _ElementFromPointBuildCache();
    void _GetFocusedElementBuildCache();
    void _CreateTreeWalker();
    void _get_ControlViewWalker();
    void _get_ContentViewWalker();
    void _get_RawViewWalker();
    void _get_RawViewCondition();
    void _get_ControlViewCondition();
    void _get_ContentViewCondition();
    void _CreateCacheRequest();
    void _CreateTrueCondition();
    void _CreateFalseCondition();
    [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition? condition);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void _SetFocus();
    void _GetRuntimeId();
    [PreserveSig] int FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? found);
    void _FindAll();
    void _FindFirstBuildCache();
    void _FindAllBuildCache();
    void _BuildUpdatedCache();
    [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
}
