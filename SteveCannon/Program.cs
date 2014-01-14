using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SteveCannon
{
    class Program
    {
        static bool checkForUpdate(FileInfo fileInfo, Uri uri)
        {
            // Build the update information.
            Update update = new Update(fileInfo, uri);

            Console.WriteLine("Check {1} for updates on {0}...", fileInfo.Name, uri);
            
            update.Check();

            Console.WriteLine("Status of {0} is {1}", fileInfo.Name, update.Action);

            return update.Exception == null && (update.Action == UpdateAction.None || update.Action == UpdateAction.Update);
        }

        /// <summary>
        /// Checks if the smod's manifest.json contains an entry for our updater.
        /// </summary>
        /// <param name="fileInfo">FileInfo of the smod archive to check</param>
        static bool checkSMod(FileInfo fileInfo)
        {
            Console.WriteLine("Check {0} for update uri...", fileInfo.Name);
            try
            {
                Uri uri = null;

                using (ZipArchive zip = ZipFile.OpenRead(fileInfo.FullName))
                {
                    ZipArchiveEntry manifestEntry = zip.Entries.FirstOrDefault(e => e.Name == "manifest.json");
                    if (manifestEntry == null)
                        return true;

                    // Fire up json magic
                    JToken manifest;

                    using (StreamReader streamReader = new StreamReader(manifestEntry.Open()))
                    using (JsonReader jsonReader = new JsonTextReader(streamReader))
                        manifest = JObject.ReadFrom(jsonReader);

                    // Read "steve_cannon"
                    JToken uriToken = manifest["steve_cannon"];

                    if (uriToken != null && uriToken.Type == JTokenType.String)
                    {
                        Console.WriteLine("Found URI; resolve...");
                        uri = new Uri(uriToken.Value<string>());
                        Console.WriteLine("Resolved.");
                    }
                }

                // Now that the file is available again...
                if (uri != null)
                    return checkForUpdate(fileInfo, uri);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("While operating on {0} an {2} exception occured: {1}", fileInfo.Name, ex.Message, ex.GetType().ToString());
                return false;
            }

            return true;
        }

        static void Main(string[] args)
        {
            // HACKS!
            string cwd = Directory.GetCurrentDirectory();
            bool launchSH = true;

            // Parse the command line
            for (int i = 0; i < args.Length; ++i)
            {
                string cmd = args[i];

                // Directory we should operate on
                if (cmd == "--dir")
                {
                    if (i == args.Length - 1)
                    {
                        Console.Error.WriteLine("Invalid --dir; expected directory following");
                        Console.ReadLine();
                        return;
                    }

                    // Otherwise...
                    cwd = args[++i];
                }
                else if (cmd == "--nolaunch")
                {
                    // vOv
                    launchSH = false;
                }
                else if (cmd == "--help")
                {
                    Console.Write("Parameters:");
                    Console.WriteLine(@"
  --help: Displays help
  --dir ""directory"": Path to your Stonehearth installation
  --nolaunch: Do not start Stonehearth after updating is complete");
                    Console.ReadLine();
                }
            }
            
            FileInfo stonehearth = new FileInfo(Path.Combine(cwd, "Stonehearth.exe"));

            // Found stonehearth?
            string modsPath;
            if (stonehearth.Exists)
                modsPath = Path.Combine(cwd, "mods");
            else
                modsPath = cwd;

            Console.WriteLine("Start updating in {0}", modsPath);

            // Are we in the executable directory?
            foreach (string smodPath in Directory.GetFiles(modsPath, "*.smod"))
            {
                try
                {
                    if (!checkSMod(new FileInfo(smodPath)))
                        launchSH = false;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Not-really-caught exception {0} happened while trying to fetch {2}: {1}", ex.GetType().ToString(), ex.Message, smodPath);
                    launchSH = false;
                }
            }

            Console.WriteLine("Processed all addons.");

            // Launch SH
            if (stonehearth != null && stonehearth.Exists && launchSH)
            {
                Console.WriteLine("Launching Stonehearth...");
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = stonehearth.FullName;
                psi.WorkingDirectory = stonehearth.DirectoryName;

                Process.Start(psi);
            }
            else
                Console.ReadLine();
        }
    }
}
