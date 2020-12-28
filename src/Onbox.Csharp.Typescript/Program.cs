﻿using CommandLine;
using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Onbox.Csharp.Typescript
{
    class Program
    {
        public class Options
        {
            [Option('s', "source", Required = true, HelpText = "The path of the folder to be watched.")]
            public string Path { get; set; }

            [Option('f', "filter", Required = true, HelpText = "The names of the assemblies to be watched.")]
            public string Filter { get; set; }

            [Option('d', "destination", Required = true, HelpText = "The destination path.")]
            public string DesitinationPath { get; set; }

            [Option('c', "controllers", Required = false, HelpText = "Map Aspnet Core Controllers.")]
            public bool MapControllers { get; set; }
        }

        private static Dictionary<Type, string> imports = new Dictionary<Type, string>();
        private static HashSet<TypeDefinition> processedTypes = new HashSet<TypeDefinition>();

        private static string output;

        private static void ClearCache()
        {
            imports.Clear();
            processedTypes.Clear();
        }

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                   .WithParsed<Options>(o =>
                   {
                       using (var watcher = new FileSystemWatcher())
                       {
                           watcher.Path = o.Path;
                           watcher.Filter = o.Filter;
                           watcher.NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;

                           output = o.DesitinationPath;

                           watcher.Created += OnModelsChanged;
                           watcher.EnableRaisingEvents = true;

                           Console.WriteLine("Press 'q' to quit.");
                           while (Console.Read() != 'q') ;
                       }
                   });
        }

        private static void OnModelsChanged(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine($"File: {e.FullPath} {e.ChangeType}");
            Task.Factory.StartNew(() =>
            {
                Task.Delay(300);
                try
                {
                    WriteAssemblyTypes(e.FullPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception: {ex.Message}");
                    Console.WriteLine($"Trace: {ex.StackTrace}");
                }
                ClearCache();
            });
        }

        private static void WriteAssemblyTypes(string fileName)
        {
            ModuleDefinition module = ModuleDefinition.ReadModule(fileName);
            foreach (TypeDefinition type in module.Types)
            {
                if (! type.IsPublic)
                    continue;

                processedTypes(type, fileName);
            }
        }

        private static byte[] GetBytes(Stream input)
        {
            byte[] buffer = new byte[16 * 1024];
            using (MemoryStream stream = new MemoryStream())
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stream.Write(buffer, 0, read);
                }
                return stream.ToArray();
            }
        }

        private static string ProcessController(TypeDefinition type, string path)
        {
            Console.WriteLine($"Mapping Controller: {type.Name}");
            var importStatments = string.Empty;
            var classBodyBuilder = new StringBuilder();


            classBodyBuilder.AppendLine();
            classBodyBuilder.AppendLine("@Injectable({");
            classBodyBuilder.AppendLine("   providedIn: 'root'");
            classBodyBuilder.AppendLine("})");
            classBodyBuilder.AppendLine($"export class {GetDefinition(type).Replace("Controller", "Service")}" + " {");
            var meths = type.Methods;
            foreach (var meth in meths)
            {
                var attr = meth.CustomAttributes;
                if (attr.FirstOrDefault(a => a.GetType().Name.Contains("HttpGet")) != null)
                {
                    var responseAttrs = attr.Where(a => a.GetType().Name.Contains("ProducesResponseType"));
                    if (responseAttrs.Any())
                    {
                        Console.WriteLine($"Endpoint: {meth.Name}");
                    }
                }
            }
            classBodyBuilder.AppendLine("}");
            var result = importStatments.Any() ? importStatments + Environment.NewLine + classBodyBuilder.ToString() : classBodyBuilder.ToString();

            //SaveTypescript(type, path, result);

            processedTypes.Add(type);
            return result;
        }

        private static string ProcessType(TypeDefinition type, string path)
        {
            if (type.IsEnum)
            {
                return ProcessEnum(type, path);
            }
            else if (type.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name.Contains("ApiController")) != null)
            {
                return ProcessController(type, path);
            }

            return ProcessClass(type, path);
        }

        private static string ProcessEnum(TypeDefinition type, string path)
        {
            var enumBodyBuilder = new StringBuilder();

            var values = type.Fields;

            enumBodyBuilder.AppendLine();
            enumBodyBuilder.AppendLine($"export enum {GetDefinition(type)}" + " {");
            var i = 0;
            foreach (var value in values)
            {
                enumBodyBuilder.AppendLine($"   {value} = {value.GetHashCode()},");
                i++;
            }
            enumBodyBuilder.AppendLine("}");

            var result = enumBodyBuilder.ToString();
            SaveTypescript(type, path, result);
            return result;
        }

        private static string ProcessClass(TypeDefinition type, string path)
        {
            var importStatments = string.Empty;

            var classBodyBuilder = new StringBuilder();
            var props = type.Properties;

            classBodyBuilder.AppendLine();
            classBodyBuilder.AppendLine($"export interface {GetDefinition(type)}" + " {");
            foreach (var prop in props)
            {
                if (ShouldImport(prop.DeclaringType) && prop.PropertyType != type)
                {
                    var importStatement = $"import {{ {GetImportName(prop.DeclaringType)} }} from \"./{GetImportName(prop.DeclaringType)}\"";
                    
                    if (importStatments == string.Empty)
                    {
                        importStatments += importStatement;
                    }
                    else if (! importStatments.Contains(importStatement))
                    {
                        importStatments += Environment.NewLine + importStatement;
                    }

                    if (!processedTypes.Contains(prop.PropertyType))
                    {
                        ProcessType(prop.DeclaringType, path);
                    }
                }
                classBodyBuilder.AppendLine($"   {prop.Name.ToLower()}: {GetPropType(prop.DeclaringType)};");
            }
            classBodyBuilder.AppendLine("}");

            var result = importStatments.Any() ? importStatments + Environment.NewLine + classBodyBuilder.ToString() : classBodyBuilder.ToString();

            SaveTypescript(type, path, result);

            processedTypes.Add(type);

            return result;
        }

        private static void SaveTypescript(TypeDefinition type, string path, string content)
        {
            var fileName = GetImportName(type);
            var enumPart = type.IsEnum ? ".enum" : "";
            var fullPath = Path.Combine(path, fileName + enumPart + ".ts");
            File.WriteAllText(fullPath, content, Encoding.UTF8);
        }

        private static bool ShouldImport(TypeDefinition type)
        {
            var isClass = type.IsClass;
            if (isClass)
            {
                return false;
            }
            else
            {
                if (type.Interfaces.Any(i => i.InterfaceType.FullName == typeof(IList).FullName))
                {
                    return false;
                }
                return true;
            }
        }

        private static string GetImportName(TypeDefinition type)
        {
            return $"{type.Name.Replace("`1", "")}";
        }

        private static string GetDefinition(TypeDefinition type)
        {
            return $"{type.Name.Replace("`1", "<T>")}";
        }

        private static string GetPropType(TypeDefinition type)
        {
            if (type.FullName == typeof(string).FullName || type.FullName == typeof(DateTime).FullName || type.FullName == typeof(DateTimeOffset).FullName)
            {
                return "string";
            }
            else if (type.FullName == typeof(int).FullName || type.FullName == typeof(double).FullName || type.FullName == typeof(float).FullName)
            {
                return "number";
            }
            else if (type.Interfaces.Any(type => type.InterfaceType.FullName == typeof(IList).FullName))
            {
                var att = type.GenericParameters.LastOrDefault();
                return $"{att.Name}[]";
            }
            else if (type.HasGenericParameters)
            {
                var att = type.GenericParameters.LastOrDefault();
                return $"{type.Name.Replace("`1", "")}<{att.Name}>";
            }
            else if (type.IsClass)
            {
                return type.Name;
            }

            return null;
        }
    }
}