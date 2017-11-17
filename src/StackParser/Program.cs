using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;

namespace StackParser
{
    public static class KnownFolder
    {
        public static readonly Guid Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
    }

    class Program
    {

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out string pszPath);

        static string s_localSymbolRoot = null;
        static string s_inputFile;
        static string s_outputFile;
        static string s_symbolServerPath;
        static bool s_keepModules = false;
        static bool s_interactive = false;
        static Dictionary<string, IDiaSession> s_pdbMap = new Dictionary<string, IDiaSession>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> s_moduleToPeFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static List<string> s_pdbFileList = new List<string>();
        static char[] s_OptionValSeparator = { ':', '=' };
        static SymStore s_symStore = null;

        // symbol server constants
        const string internalSymbolServer = "http://symweb";
        const string publicSymbolServer = "http://msdl.microsoft.com/download/symbols";

        static void PrintUsage()
        {
            Console.WriteLine("StackParser [/pdbs {pdbFiles}] [/modules {PE files}]");
            Console.WriteLine("            [/keep] [/in inputFile] [/out outputfile] [/symsrv definition]");
            Console.WriteLine();
            Console.WriteLine(" All arguments are optional, but at least one pdb file or one PE file need to be defined.");
            Console.WriteLine();
            Console.WriteLine("    /pdbs {pdbFiles}      A non empty list of pdb files to be used.");
            Console.WriteLine("                          Use wildcards to load more files from a single directory.");
            Console.WriteLine("    /modules {PE files}   A non empty list of loaded modules used.");
            Console.WriteLine("                          Symbols are fetched from the symbol store.");
            Console.WriteLine("    /keep                 Keep the pdb files.");
            Console.WriteLine("                          (default: all downloaded symbol files are deleted).");
            Console.WriteLine("    /in <file>            Input to operate on (default: stdin).");
            Console.WriteLine("    /out <file>           Append output to file (default: stdout).");
            Console.WriteLine("    /symsrv <definition> \"Standard\"definition for symbol server cache.");
            Console.WriteLine("                          (Implies /keep).");

            Console.WriteLine();
            Console.WriteLine("    Examples:");
            Console.WriteLine(" StackParser /pdbs app.pdb SharedLibrary.pdb < log.txt");
            Console.WriteLine("     Uses existing PDB files to convert log.txt and writes result to console.");
            Console.WriteLine();
            Console.WriteLine(" StackParser /pdbs app.pdb /modules Lib.dll /in log.txt /out symlog.txt");
            Console.WriteLine("     Uses existing PDB (app.pdb) and looks up pdb file for Lib.dll;");
            Console.WriteLine("     reads input from log.txt and writes result to symlog.txt.");
            Console.WriteLine();
            Console.WriteLine(@" StackParser /modules foo.dll /in log.txt /symserver srv*c:\symbols*http://msdl.microsoft.com/download/symbols");
            Console.WriteLine(@"     Converts log.txt by getting symbols from public symbol server");
            Console.WriteLine(@"     (with local cache at c:\symbols).");
            Console.WriteLine();
        }

        static int GetOptionValue(string[] args, string curArg, int argPos, out string value)
        {
            int j = curArg.IndexOfAny(s_OptionValSeparator);
            if (j != -1 && j < curArg.Length-1)
            {
                value = curArg.Substring(j + 1);
                return argPos;
            }
            if (argPos + 1 < args.Length)
            {
                value = args[++argPos];
                return argPos;
            }

            value = null;
            return argPos;
        }
        static int CommandLineParser(string[] args)
        {

            bool inPdbs = false;
            bool inModules = false;

            for (int i = 0; i < args.Length; ++i)
            {
                string curArg = args[i];
                if (curArg.StartsWith("/") || curArg.StartsWith("-"))
                {
                    //consume option character
                    curArg = curArg.Substring(1).ToLower();
                    if (curArg.StartsWith("pdbs"))
                    {
                        inPdbs = true;
                        inModules = false;
                    }
                    else if (curArg.StartsWith("modules"))
                    {
                        inPdbs = false;
                        inModules = true;
                    }
                    else if (curArg == "keep")
                    {
                        s_keepModules = true;
                    }
                    else if (curArg == "in")
                    {
                        i = GetOptionValue(args, curArg, i, out s_inputFile);
                    }
                    else if (curArg == "out")
                    {
                        i = GetOptionValue(args, curArg, i, out s_outputFile);
                    }
                    else if (curArg == "symsrv")
                    {
                        i = GetOptionValue(args, curArg, i, out s_symbolServerPath);
                    }
                    else
                    {
                        Console.Error.WriteLine("Unexpected Option: {0}", curArg);
                        return -1;
                    }
                    continue;
                }

                if (inPdbs)
                {
                    s_pdbFileList.Add(curArg);
                }
                else if (inModules)
                {
                    string moduleName = Path.GetFileNameWithoutExtension(curArg);

                    s_moduleToPeFileMap[moduleName] = curArg;
                }
                else
                {
                    Console.Error.WriteLine("unexpected input");
                    return -1;
                }
            }

            // no point in running without any debug info
            if (s_pdbFileList.Count == 0 && s_moduleToPeFileMap.Count == 0)
                return -1;
            return 1;
        }

        public static IEnumerable<String> ParseText(String line, Char delimiter, Char textQualifier)
        {

            if (line == null)
                yield break;

            else
            {
                Char prevChar = '\0';
                Char nextChar = '\0';
                Char currentChar = '\0';

                Boolean inString = false;

                StringBuilder token = new StringBuilder();

                for (int i = 0; i < line.Length; i++)
                {
                    currentChar = line[i];

                    if (i > 0)
                        prevChar = line[i - 1];
                    else
                        prevChar = '\0';

                    if (i + 1 < line.Length)
                        nextChar = line[i + 1];
                    else
                        nextChar = '\0';

                    if (currentChar == textQualifier && (prevChar == '\0' || prevChar == delimiter) && !inString)
                    {
                        inString = true;
                        continue;
                    }

                    if (currentChar == textQualifier && (nextChar == '\0' || nextChar == delimiter) && inString)
                    {
                        inString = false;
                        continue;
                    }

                    if (currentChar == delimiter && !inString)
                    {
                        yield return token.ToString();
                        token = token.Remove(0, token.Length);
                        continue;
                    }

                    token = token.Append(currentChar);

                }

                yield return token.ToString();

            }
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                Console.WriteLine();
                Console.WriteLine("Enter arguments: ");
                s_interactive = true;
                args = ParseText(Console.ReadLine(), ' ', '"').ToArray();
                if (args.Length == 0)
                {
                    return;
                }
            }


            if (CommandLineParser(args) == -1)
            {
                PrintUsage();
                return;
            }

            foreach (string pdbName in s_pdbFileList)
            {
                try
                {
                    string moduleName = Path.GetFileNameWithoutExtension(pdbName);
                    if (moduleName.Contains("*") || moduleName.Contains("?"))
                    {
                        string pdbPath = Path.GetDirectoryName(pdbName);
                        if (string.IsNullOrEmpty(pdbPath))
                        {
                            pdbPath = Directory.GetCurrentDirectory();
                        }
                        string pdbSearchPattern = Path.GetFileName(pdbName);
                        foreach (string fileName in Directory.GetFiles(pdbPath, pdbSearchPattern))
                        {
                            moduleName = Path.GetFileNameWithoutExtension(fileName);
                            var pdbSession = DiaHelper.LoadPDB(fileName);
                            if (pdbSession != null)
                                s_pdbMap[moduleName] = pdbSession;
                        }
                    }
                    else
                    {
                        string fullName = Path.GetFullPath(pdbName);
                        if (!File.Exists(fullName))
                        {
                            continue;
                        }
                        var pdbSession = DiaHelper.LoadPDB(fullName);
                        if (pdbSession != null)
                            s_pdbMap[moduleName] = pdbSession;
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e.Message);
                    return;
                }
            }

            List<string> tmpFileList = new List<string>();

            System.IO.StreamReader sr = null;
            System.IO.StreamWriter sw = null;
            System.IO.TextReader tr;
            System.IO.TextWriter tw = null;
            bool tsvMode = false;
            if (!String.IsNullOrEmpty(s_inputFile) && File.Exists(s_inputFile))
            {
                tsvMode = Path.GetExtension(s_inputFile) == ".tsv";
                tr = sr = new System.IO.StreamReader(s_inputFile);
            }
            else
            {
                tr = Console.In;
            }

            if (!String.IsNullOrEmpty(s_outputFile))
            {
                // create file (appened if it exists)
                tw = sw = new StreamWriter(s_outputFile, true);

            }
            if (tw == null)
            {
                tw = Console.Out;
            }

            string line;

            // If we are using a pre-defined
            if (!String.IsNullOrEmpty(s_symbolServerPath))
            {
                s_keepModules = true;
            }

            // Create a (unique) local temp directory
            //            string tempDir = Environment.GetEnvironmentVariable("TEMP");

            if (s_moduleToPeFileMap.Count > 0)
            {
                if (String.IsNullOrEmpty(s_symbolServerPath))
                {
                    string tempDir;
                    SHGetKnownFolderPath(KnownFolder.Downloads, 0, IntPtr.Zero, out tempDir);
                    if (tempDir == null)
                    {
                        Console.Error.WriteLine("Cannot get path for download directory");
                        tempDir = @"C:\SymbStore";
                    }

                    if (!String.IsNullOrEmpty(tempDir))
                    {
                        int i = 0;
                        bool created = false;
                        do
                        {
                            s_localSymbolRoot = Path.Combine(tempDir, String.Concat("PDBTemp", i.ToString()));
                            i++;
                            if (!s_keepModules && Directory.Exists(s_localSymbolRoot))
                                continue;

                            try
                            {
                                Directory.CreateDirectory(s_localSymbolRoot);
                                created = true;
                            }
                            catch (Exception)
                            {
                            }
                        } while (!created);
                    }
                    // construct our own path
                    s_symbolServerPath = "srv*" + s_localSymbolRoot + "*" + internalSymbolServer;
                }
            }
            int lineCount = -1;
            while((line = tr.ReadLine()) != null)
            {
                const string separator = "!<BaseAddress>+0x";
                string moduleStr = "";
                string moduleName = "";
                string rvaStr = "";
                string rva = "";
                string prefix = "at ";
                string suffix = "";
                string methodName = "";
                lineCount++;
                if (tsvMode)
                {
                    if (lineCount == 0) continue;
                    string[] frags = line.Split('\t');
                    if (frags.Length >= 4)
                    {
                        moduleStr = frags[1];
                        moduleName = moduleStr.Replace(".dll", "").Replace(".exe", "");
                        rvaStr = frags[3];
                        rva = rvaStr.Replace("0x", "");
                        if (frags[2] != "null")
                        {
                            methodName = frags[2];
                        }
                    }
                }
                else
                {
                    string[] frags = line.Split(new string[] { separator }, StringSplitOptions.RemoveEmptyEntries);
                    if (frags.Length == 2)
                    {
                        moduleStr = frags[0];
                        int idx = moduleStr.LastIndexOf(' ');
                        if (idx < 0) idx = -1;
                        moduleName = moduleStr.Substring(idx + 1);
                        prefix = moduleStr.Substring(0, idx + 1);

                        rvaStr = frags[1];
                        idx = rvaStr.IndexOf(' ');
                        if (idx < 0) idx = rvaStr.Length;
                        rva = rvaStr.Substring(0, idx);
                        suffix = rvaStr.Substring(idx);
                    }
                    else
                    {
                        tw.WriteLine(line);
                    }
                }

                if (!String.IsNullOrEmpty(moduleName) )
                {
                    uint rvaNum;
                    IDiaSession pdbSession = null;
                    if (!s_pdbMap.TryGetValue(moduleName, out pdbSession))
                    {
                        string peFileName;
                        if (s_moduleToPeFileMap.TryGetValue(moduleName, out peFileName))
                        {
                            // Try to get the pdb file from the symbol store
                            string pdbFileName = GetPdbFileForModule(peFileName);

                            if (!String.IsNullOrEmpty(pdbFileName))
                            {

                                if (!tmpFileList.Contains(pdbFileName))
                                    tmpFileList.Add(pdbFileName);
                                // Load pdb
                                pdbSession = DiaHelper.LoadPDB(pdbFileName);

                                // cache session
                                if (pdbSession != null)
                                    s_pdbMap[moduleName] = pdbSession;
                            }

                        }
                    }

                    string tmpMethodName = null;

                    if (pdbSession == null || (rvaNum = HexToUint(rva)) == 0 || ((tmpMethodName = DiaHelper.GetMethodName(pdbSession, rvaNum)) == null))
                    {
                        if (!String.IsNullOrEmpty(methodName))
                        {
                            tw.WriteLine("{0}{1}!{2} {3}",prefix,moduleStr, methodName, rvaStr);
                        }
                        else
                        {
                            tw.WriteLine("{0}{1}{2}", moduleStr, separator, rvaStr);
                        }
                        continue;
                    }
                    if (!String.IsNullOrEmpty(tmpMethodName))
                    {
                        methodName = tmpMethodName;
                    }

                    tw.WriteLine("{0}{1}!{2}{3}", prefix, moduleName, methodName, suffix);
                }
                else
                {
                    tw.WriteLine(line);
                }
               

            }

            // clean up

            if (!String.IsNullOrEmpty(s_localSymbolRoot) && tmpFileList.Count > 0)
            {
                if (s_keepModules)
                {
                    Console.WriteLine("Downloaded symbol file{0}:", tmpFileList.Count != 1 ? "s" : "");
                    foreach (string tmpFile in tmpFileList)
                    {
                        Console.WriteLine(tmpFile);
                    }

                }
                else
                {
                    foreach (KeyValuePair<string, IDiaSession> mapPair in s_pdbMap)
                    {
                        DiaHelper.ReleaseSession(mapPair.Value);
                    }

                    // reset pdbMap (drop references to IDiaSession)
                    s_pdbMap = new Dictionary<string, IDiaSession>();
                    // reset symbol store (relinquish potential references to just downloaded pdb files)
                    s_symStore = null;

                    // let the GC finalize (release) COM objects
                    GC.Collect();
                    GC.Collect();

                    foreach (string tmpFile in tmpFileList)
                    {
                        try
                        {
                            File.Delete(tmpFile);
                        }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine("Failed to delete {0}", tmpFile);
                            Console.Error.WriteLine(e.Message);
                        }
                    }
                    try
                    {
                        Directory.Delete(s_localSymbolRoot, true);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("Failed to delete directory {0}", s_localSymbolRoot);
                        Console.Error.WriteLine(e.Message);
                    }
                }
            }

            if (sr != null)
            {
                sr.Close();
            }

            if (sw != null)
            {
                sw.Close();
            }
            if (s_interactive)
            {
                Console.WriteLine("Press a key to exit.");
                Console.ReadLine();
            }
        }

        static CodeViewDebugData CvDataFromPE(string fileName)
        {
            PEReader per = null;
            try
            {
                // Inspect PE to get the PDB information
                per = new PEReader(new System.IO.FileStream(fileName, FileMode.Open));
            }
            catch (Exception)
            {
            }

            return (per != null) ? per.CodeViewDebugData : null;
        }

        static Dictionary<string, string> s_pdbFileForModule = new Dictionary<string, string>();
        static string GetPdbFileForModule(string fileName)
        {
            string retVal;

            // look in the "cache" of already successfully opened
            if (s_pdbFileForModule.TryGetValue(fileName, out retVal))
            {
                return retVal;
            }

            retVal = null;
            CodeViewDebugData cvdd = CvDataFromPE(fileName);

            if (cvdd != null)
            {
                if (s_symStore == null)
                    s_symStore = SymStore.FromPath(s_symbolServerPath);

                if (s_symStore != null)
                {
                    Stream pdbStream;
                    string indexString = cvdd.Signature.ToString().Replace("-", "").ToUpper() + cvdd.Age.ToString();
                    string localFileName = Path.GetFileName(cvdd.PdbPath);
                    SymStoreResult symResult = new SymStoreResult();
                    if (s_symStore.TryGetFile(localFileName, indexString, symResult, out pdbStream))
                    {
                        retVal = symResult.CachedPath;
                    }
                }

            }

            s_pdbFileForModule.Add(fileName, retVal);
            return retVal;
        }

        static uint HexToUint(string hexStr)
        {
            try
            {
                return Convert.ToUInt32(hexStr, 16);
            }
            catch(FormatException)
            {
                return 0;
            }
        }

    }
}
