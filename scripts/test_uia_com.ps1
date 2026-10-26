$code = @"
using System;
using System.Runtime.InteropServices;

namespace TestUia
{
    [ComImport]
    [Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    public class CUIAutomationClass {}

    public class Tester
    {
        public static void Run()
        {
            var uia = new CUIAutomationClass();
            Console.WriteLine("CUIAutomation instantiated successfully: " + (uia != null));
        }
    }
}
"@

Add-Type -TypeDefinition $code -Language CSharp
[TestUia.Tester]::Run()
