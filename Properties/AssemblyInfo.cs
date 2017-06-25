using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Permissions;

/* 
 * Tells the compiler to make sure the whole assembly is CLS Compliant,
 * this is nessisary for users that may not be using C# to access and use this assembly.
 */
[assembly: CLSCompliant(true)]

// General Information about an assembly is controlled through the following set of attributes.
// Change these attribute values to modify the information associated with an assembly.
[assembly: AssemblyTitle("VIEApps NGX")]
[assembly: AssemblyDescription("Core components of VIEApps NGX")]
[assembly: AssemblyDefaultAlias("VIEApps.Components.Cache")]
[assembly: AssemblyConfiguration("Production/Stable")]
[assembly: AssemblyCompany("VIEApps.net")]
[assembly: AssemblyProduct("VIEApps NGX")]
[assembly: AssemblyCopyright("Copyright © 2017 VIEApps.net")]
[assembly: AssemblyTrademark("VIEApps")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible to COM components.
// If you need to access a type in this assembly from COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("1C93730E-B0B3-410F-BADE-C03AB45EDD1F")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
[assembly: AssemblyVersion("10.1")]
[assembly: AssemblyFileVersion("10.1")]
[assembly: AssemblyInformationalVersion("10.1.nemc-2017.06.24")]
