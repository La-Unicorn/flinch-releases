$code = @"
using System;
using System.Runtime.InteropServices;

namespace TestUia
{
    [ComImport]
    [Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    public class CUIAutomationClass {}

    public struct TagRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac0ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomation
    {
        int CompareElements(IntPtr el1, IntPtr el2);
        int CompareRuntimeIds(IntPtr arr1, IntPtr arr2);
        void GetRootElement([Out] out IUIAutomationElement root);
        void ElementFromHandle(IntPtr hwnd, [Out] out IUIAutomationElement element);
        void ElementFromPoint(long pt, [Out] out IUIAutomationElement element);
        void GetFocusedElement([Out] out IUIAutomationElement element);
        void GetRootElementBuildCache(IntPtr cacheRequest, [Out] out IUIAutomationElement root);
        void ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, [Out] out IUIAutomationElement element);
        void ElementFromPointBuildCache(long pt, IntPtr cacheRequest, [Out] out IUIAutomationElement element);
        void GetFocusedElementBuildCache(IntPtr cacheRequest, [Out] out IUIAutomationElement element);
        void CreateTreeWalker(IntPtr condition, [Out] out IUIAutomationTreeWalker walker);
        void get_ControlViewWalker([Out] out IUIAutomationTreeWalker walker);
        void get_ContentViewWalker([Out] out IUIAutomationTreeWalker walker);
        void get_RawViewWalker([Out] out IUIAutomationTreeWalker walker);
    }

    [ComImport]
    [Guid("995e2fed-f641-4614-b62d-adf1e272be7c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationTreeWalker
    {
        void GetParentElement(IUIAutomationElement element, [Out] out IUIAutomationElement parent);
        void GetFirstChildElement(IUIAutomationElement element, [Out] out IUIAutomationElement first);
        void GetLastChildElement(IUIAutomationElement element, [Out] out IUIAutomationElement last);
        void GetNextSiblingElement(IUIAutomationElement element, [Out] out IUIAutomationElement next);
        void GetPreviousSiblingElement(IUIAutomationElement element, [Out] out IUIAutomationElement previous);
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out IntPtr runtimeId);
        void FindFirst(int scope, IntPtr condition, [Out] out IUIAutomationElement found);
        void FindAll(int scope, IntPtr condition, [Out] out IntPtr found);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCurrentPattern();
        void GetCachedPattern();
        void GetCachedPatternAs();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternEx();
        void GetCachedPatternEx();
        void GetCurrentPropertyEx();
        void GetCachedPropertyEx();
        void get_CurrentProcessId([Out] out int retVal);
        void get_CurrentControlType([Out] out int retVal);
        void get_CurrentLocalizedControlType([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentName([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentAcceleratorKey([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentAccessKey([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentHasKeyboardFocus([Out] out int retVal);
        void get_CurrentIsKeyboardFocusable([Out] out int retVal);
        void get_CurrentIsEnabled([Out] out int retVal);
        void get_CurrentAutomationId([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentClassName([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentHelpText([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentCulture([Out] out int retVal);
        void get_CurrentIsControlElement([Out] out int retVal);
        void get_CurrentIsContentElement([Out] out int retVal);
        void get_CurrentIsPassword([Out] out int retVal);
        void get_CurrentNativeWindowHandle([Out] out IntPtr retVal);
        void get_CurrentItemType([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentIsOffscreen([Out] out int retVal);
        void get_CurrentOrientation([Out] out int retVal);
        void get_CurrentFrameworkId([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentIsRequiredForForm([Out] out int retVal);
        void get_CurrentItemStatus([Out, MarshalAs(UnmanagedType.BStr)] out string retVal);
        void get_CurrentBoundingRectangle([Out] out TagRect retVal);
    }

    public class Tester
    {
        public static void Run()
        {
            var uia = (IUIAutomation)new CUIAutomationClass();
            IUIAutomationElement root;
            uia.GetRootElement(out root);
            string name;
            root.get_CurrentName(out name);
            TagRect rect;
            root.get_CurrentBoundingRectangle(out rect);
            Console.WriteLine(string.Format("Root element name: '{0}', rect: [{1},{2} - {3},{4}]", name, rect.left, rect.top, rect.right, rect.bottom));

            IUIAutomationTreeWalker walker;
            uia.get_ControlViewWalker(out walker);
            IUIAutomationElement child;
            walker.GetFirstChildElement(root, out child);
            if (child != null)
            {
                string childName, childType;
                child.get_CurrentName(out childName);
                child.get_CurrentLocalizedControlType(out childType);
                Console.WriteLine(string.Format("First child: '{0}' ({1})", childName, childType));
            }
        }
    }
}
"@

Add-Type -TypeDefinition $code -Language CSharp
[TestUia.Tester]::Run()
