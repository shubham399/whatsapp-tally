using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("TallyWhatsappSender")]
[assembly: AssemblyDescription("Sends TallyPrime invoices to WhatsApp Web through Chrome automation.")]
[assembly: AssemblyProduct("TallyWhatsappSender")]
[assembly: AssemblyVersion("1.0.10.0")]
[assembly: AssemblyFileVersion("1.0.10.0")]

// Only the WhatsAppSender class is exposed to COM.
[assembly: ComVisible(false)]
[assembly: Guid("7a3f1c2e-5b4d-4e6f-9a8b-1c2d3e4f5a6b")]
