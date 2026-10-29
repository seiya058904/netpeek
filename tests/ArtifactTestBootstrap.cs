using System;
using System.IO;
using System.Reflection;

// Loads the requested artifact before any test type resolves. No production source is compiled.
internal static class ArtifactTestBootstrap
{
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            string target = Path.GetFullPath(args[0]);
            Assembly artifact = Assembly.LoadFrom(target);
            if (!String.Equals(artifact.Location, target, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected artifact location: " + artifact.Location);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) { return new AssemblyName(e.Name).Name == artifact.GetName().Name ? artifact : null; };
            Console.WriteLine("ARTIFACT loaded=" + artifact.Location + "; MVID=" + artifact.ManifestModule.ModuleVersionId);
            Type test = Assembly.GetExecutingAssembly().GetType(args[1], true);
            int result = (int)test.GetMethod("Main", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) {
                if (loaded.GetName().Name == artifact.GetName().Name && !String.Equals(loaded.Location, target, StringComparison.OrdinalIgnoreCase)) throw new Exception("A different business assembly was loaded: " + loaded.Location);
            }
            return result;
        } catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
}
