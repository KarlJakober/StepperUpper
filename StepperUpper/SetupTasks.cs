﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

using AirBreather.IO;
using AirBreather.Text;

using BethFile;

using static StepperUpper.Cleaner;

namespace StepperUpper
{
    internal static class SetupTasks
    {
        internal static Task DispatchAsync(XElement taskElement, IReadOnlyDictionary<string, FileInfo> knownFiles, DirectoryInfo dumpDirectory, DirectoryInfo steamInstallDirectory, IReadOnlyDictionary<Md5Checksum, string> checkedFiles, IReadOnlyDictionary<string, TaskCompletionSource<object>> otherTasks)
        {
            switch (taskElement.Name.LocalName)
            {
                case "ExtractArchive":
                    return ExtractArchiveAsync(taskElement, knownFiles, dumpDirectory, steamInstallDirectory);

                case "TweakINI":
                    return Task.Run(() => WriteINI(taskElement, dumpDirectory));

                case "CopyFile":
                    return Task.Run(() => CopyFile(taskElement, dumpDirectory, checkedFiles));

                case "Embedded":
                    return WriteEmbeddedFileAsync(taskElement, dumpDirectory);

                case "Clean":
                    return Task.Run(() => DoCleaningAsync(GetPlugins(taskElement, knownFiles, dumpDirectory)));

                case "CreateEmptyFolder":
                    Directory.CreateDirectory(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("Path").Value));
                    return Task.CompletedTask;

                case "RunProcess":
                    return ProcessRunner.RunProcessAsync(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("ExecutablePath").Value),
                                                         taskElement.Elements("Argument").Select(arg => GetArgument(arg, dumpDirectory)).ToArray());

                case "DeleteFolder":
                    return Program.DeleteDirectoryAsync(new DirectoryInfo(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("Path").Value)));

                case "MoveFolder":
                    return Program.MoveDirectoryAsync(new DirectoryInfo(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("From").Value)),
                                                      new DirectoryInfo(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("To").Value)));
            }

            throw new NotSupportedException("Task type " + taskElement.Name.LocalName + " is not supported.");
        }

        private static async Task ExtractArchiveAsync(XElement taskElement, IReadOnlyDictionary<string, FileInfo> knownFiles, DirectoryInfo dumpDirectory, DirectoryInfo steamInstallDirectory)
        {
            string randomFileName = Path.GetRandomFileName();
            IEnumerable<XElement> elements = taskElement.Elements();

            string givenFile = taskElement.Attribute("ArchiveFile").Value;
            bool explicitDelete = true;

            DirectoryInfo tempDirectory;

            // slight hack to make the STEP XML file much more bearable.
            XAttribute simpleMO = taskElement.Attribute("SimpleMO");
            if (simpleMO != null)
            {
                explicitDelete = false;
                tempDirectory = new DirectoryInfo(Path.Combine(dumpDirectory.FullName, "ModOrganizer", "mods", givenFile));
            }
            else
            {
                // previously, this added some user-friendly identifying marks to the folder name,
                // but that caused some paths to exceed the max path length unnecessarily.
                tempDirectory = new DirectoryInfo(Path.Combine(dumpDirectory.FullName, randomFileName));
            }

            tempDirectory.Create();
            await SevenZipExtractor.ExtractArchiveAsync(knownFiles[givenFile].FullName, tempDirectory).ConfigureAwait(false);

            switch (simpleMO?.Value)
            {
                case "Single":
                    DirectoryInfo singleSub = tempDirectory.GetDirectories().Single();

                    // rename randomly to ensure no temporary conflicts
                    singleSub.MoveTo(Path.Combine(tempDirectory.FullName, randomFileName));
                    elements = new[] { new XElement("MapFolder", new XAttribute("From", randomFileName), new XAttribute("To", tempDirectory.FullName)) }.Concat(elements);
                    break;

                case "SingleData":
                    DirectoryInfo singleData = tempDirectory.GetDirectories().Where(dir => "data".Equals(dir.Name, StringComparison.OrdinalIgnoreCase)).Single();

                    // rename randomly to ensure no temporary conflicts
                    singleData.MoveTo(Path.Combine(tempDirectory.FullName, randomFileName));
                    elements = new[] { new XElement("MapFolder", new XAttribute("From", randomFileName), new XAttribute("To", tempDirectory.FullName)) }.Concat(elements);
                    break;
            }

            foreach (XElement element in elements)
            {
                switch (element.Name.LocalName)
                {
                    case "MapFolder":
                    {
                        string givenFromPath = element.Attribute("From")?.Value ?? String.Empty;
                        string givenToPath = element.Attribute("To").Value;
                        string toPath = Path.Combine(dumpDirectory.FullName, givenToPath);
                        DirectoryInfo toDirectory = new DirectoryInfo(toPath);
                        toDirectory.Parent.Create();

                        if (givenFromPath.Length == 0)
                        {
                            explicitDelete = false;
                        }

                        string fromPath = Path.Combine(tempDirectory.FullName, givenFromPath);
                        DirectoryInfo fromDirectory = new DirectoryInfo(fromPath);

                        await Program.MoveDirectoryAsync(fromDirectory, toDirectory).ConfigureAwait(false);
                        break;
                    }

                    case "MapFile":
                    {
                        string givenFromPath = element.Attribute("From").Value;
                        string givenToPath = element.Attribute("To").Value;

                        string fromPath = Path.Combine(tempDirectory.FullName, givenFromPath);
                        string toPath = Path.Combine(dumpDirectory.FullName, givenToPath);

                        FileInfo toFile = new FileInfo(toPath);
                        toFile.Directory.Create();
                        if (toFile.Exists)
                        {
                            toFile.Delete();
                            toFile.Refresh();
                        }

                        File.Move(fromPath, toPath);
                        break;
                    }

                    case "Hide":
                    {
                        // "hide"... heh...
                        string folderToHide = element.Attribute("Folder")?.Value;
                        string pathToHide = Path.Combine(dumpDirectory.FullName, folderToHide ?? element.Attribute("File").Value);
                        if (folderToHide != null)
                        {
                            Directory.Move(pathToHide, pathToHide + ".mohidden");
                        }
                        else
                        {
                            File.Move(pathToHide, pathToHide + ".mohidden");
                        }

                        break;
                    }

                    case "Optional":
                    {
                        FileInfo file = new FileInfo(Path.Combine(dumpDirectory.FullName, element.Attribute("File").Value));
                        file.MoveTo(Path.Combine(file.Directory.CreateSubdirectory("optional").FullName, file.Name));
                        break;
                    }

                    default:
                        throw new NotSupportedException("Unsupported element: " + element.Name.LocalName);
                }
            }

            if (explicitDelete)
            {
                await Program.DeleteDirectoryAsync(tempDirectory).ConfigureAwait(false);
            }
        }

        private static void WriteINI(XElement taskElement, DirectoryInfo dumpDirectory)
        {
            FileInfo iniFile = new FileInfo(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("File").Value));
            iniFile.Directory.Create();

            foreach (XElement setElement in taskElement.Elements("Set"))
            {
                NativeMethods.WritePrivateProfileString(sectionName: setElement.Attribute("Section").Value,
                                                        propertyName: setElement.Attribute("Property").Value,
                                                        value: setElement.Attribute("Value").Value,
                                                        iniFilePath: iniFile.FullName);
            }
        }

        private static void CopyFile(XElement taskElement, DirectoryInfo dumpDirectory, IReadOnlyDictionary<Md5Checksum, string> checkedFiles)
        {
            XAttribute fromAttribute = taskElement.Attribute("From");
            XAttribute fileAttribute = taskElement.Attribute("File");
            FileInfo fromFile = null;
            if (fromAttribute != null)
            {
                fromFile = new FileInfo(Path.Combine(dumpDirectory.FullName, fromAttribute.Value));
            }
            else
            {
                // TODO: in reality, this may come from an earlier task.
                fromFile = new FileInfo(checkedFiles[new Md5Checksum(fileAttribute.Value)]);
            }

            FileInfo toFile = new FileInfo(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("To").Value));
            toFile.Directory.Create();
            fromFile.CopyTo(toFile.FullName, true);
        }

        private static async Task WriteEmbeddedFileAsync(XElement taskElement, DirectoryInfo dumpDirectory)
        {
            FileInfo file = new FileInfo(Path.Combine(dumpDirectory.FullName, taskElement.Attribute("File").Value));
            file.Directory.Create();
            Encoding encoding = null;
            switch (taskElement.Attribute("Encoding")?.Value)
            {
                case null:
                    break;

                case "UTF8NoBOM":
                    encoding = EncodingEx.UTF8NoBOM;
                    break;

                default:
                    throw new NotSupportedException("I don't know what encoding to use for " + taskElement.Attribute("Encoding").Value);
            }

            using (FileStream stream = AsyncFile.CreateSequential(file.FullName))
            {
                if (encoding != null)
                {
                    using (StreamWriter writer = new StreamWriter(stream, encoding, 4096, true))
                    {
                        foreach (string line in taskElement.Elements("Line").Select(l => l.Value))
                        {
                            await writer.WriteLineAsync(line).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    byte[] buf = Convert.FromBase64String(taskElement.Value);
                    await stream.WriteAsync(buf, 0, buf.Length).ConfigureAwait(false);
                }
            }
        }

        private static IEnumerable<PluginForCleaning> GetPlugins(XElement taskElement, IReadOnlyDictionary<string, FileInfo> knownFiles, DirectoryInfo dumpDirectory)
        {
            var pluginsForCleaning = new List<PluginForCleaning>();
            foreach (var el in taskElement.Elements("Plugin"))
            {
                FileInfo fl;
                string outputPath;
                string name;

                string inputPath = el.Attribute("Path")?.Value;
                string dirtyFile = el.Attribute("DirtyFile")?.Value;
                if (inputPath != null)
                {
                    fl = new FileInfo(Path.Combine(dumpDirectory.FullName, inputPath));
                    outputPath = name = fl.FullName;
                }
                else if (dirtyFile != null)
                {
                    fl = knownFiles[name = dirtyFile];
                    FileInfo outputFile = new FileInfo(Path.Combine(dumpDirectory.FullName, el.Attribute("OutputPath").Value));
                    outputFile.Directory.Create();
                    outputPath = outputFile.FullName;
                }
                else
                {
                    fl = knownFiles[name = el.Attribute("CleanFile").Value];
                    outputPath = null;
                }

                if (el.Attribute("WaitFor") != null)
                {
                    throw new NotSupportedException("Plugin elements no longer support the WaitFor attribute as of 0.9.1.0.");
                }

                yield return new PluginForCleaning(
                    name: name,
                    outputFilePath: outputPath,
                    dirtyFile: fl,
                    parentNames: el.Elements("Master").Select(el2 => el2.Attribute("File").Value),
                    recordsToDelete: TokenizeIds(el.Element("Delete")?.Attribute("Ids").Value),
                    recordsToUDR: TokenizeIds(el.Element("UDR")?.Attribute("Ids").Value),
                    fieldsToDelete: el.Elements("RemoveField").Select(el2 => new FieldToDelete(UInt32.Parse(el2.Attribute("RecordId").Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), new B4S(el2.Attribute("FieldType").Value))));
            }
        }

        private static IEnumerable<uint> TokenizeIds(string ids) => Program.Tokenize(ids).Select(id => UInt32.Parse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        private static string GetArgument(XElement arg, DirectoryInfo dumpDirectory)
        {
            switch (arg.Attribute("Type")?.Value)
            {
                case null:
                    return arg.Value;

                case "PathUnderOutputFolder":
                    return Path.Combine(dumpDirectory.FullName, arg.Value);
            }

            throw new NotSupportedException("Argument type " + arg.Attribute("Type")?.Value + " was not recognized.");
        }
    }
}
