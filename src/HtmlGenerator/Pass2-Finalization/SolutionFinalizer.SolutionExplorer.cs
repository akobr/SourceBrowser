/*
    Copyright 2017 Ales Kobr

    Copyright 2015 Kirill Osenkov, Vladimir Matveev

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0


    This file has been modified by Ales Kobr to add support for 
    excluded projects.
*/

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;
using Folder = Microsoft.SourceBrowser.HtmlGenerator.Folder<Microsoft.CodeAnalysis.Project>;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class SolutionFinalizer
    {
        private void WriteSolutionExplorer(ISet<string> excludedProjects, Folder root = null)
        {
            if (root == null)
            {
                return;
            }

            Sort(root);

            using (var writer = new StreamWriter(Path.Combine(SolutionDestinationFolder, Constants.SolutionExplorer + ".html")))
            {
                Log.Write("Solution Explorer...");
                Markup.WriteSolutionExplorerPrefix(writer);
                WriteFolder(root, writer, excludedProjects);
                Markup.WriteSolutionExplorerSuffix(writer);
            }
        }

        private void Sort(Folder<Project> root, Comparison<string> customRootSorter = null)
        {
            if (Configuration.FlattenSolutionExplorer)
            {
                if (customRootSorter == null)
                {
                    customRootSorter = (l, r) => StringComparer.OrdinalIgnoreCase.Compare(l, r);
                }

                root.Sort((l, r) => customRootSorter(l.AssemblyName, r.AssemblyName));
            }
            else
            {
                root.Sort((l, r) => StringComparer.OrdinalIgnoreCase.Compare(l.Name, r.Name));
            }
        }

        private void WriteFolder(Folder folder, StreamWriter writer, ISet<string> excludedProjects)
        {
            if (folder.Folders != null)
            {
                foreach (var subfolder in folder.Folders.Values)
                {
                    writer.WriteLine(@"<div class=""folderTitle"">{0}</div><div class=""folder"">", subfolder.Name);
                    WriteFolder(subfolder, writer, excludedProjects);
                    writer.WriteLine("</div>");
                }
            }

            if (folder.Items != null)
            {
                foreach (var project in folder.Items)
                {
                    string projectName = Path.GetFileName(project.FilePath ?? "null");
                    if (excludedProjects.Contains(projectName))
                    {
                        continue;
                    }

                    WriteProject(project.AssemblyName, writer);
                }
            }
        }

        private void WriteProject(string assemblyName, StreamWriter writer)
        {
            var projectExplorerText = GetProjectExplorerText(assemblyName);
            if (!string.IsNullOrEmpty(projectExplorerText))
            {
                writer.WriteLine(projectExplorerText);
            }
        }

        private string GetProjectExplorerText(string assemblyName)
        {
            var fileName = Path.Combine(SolutionDestinationFolder, assemblyName, Constants.ProjectExplorer + ".html");
            if (!File.Exists(fileName))
            {
                return null;
            }

            var text = File.ReadAllText(fileName);
            var startText = "<div id=\"rootFolder\"";
            var start = text.IndexOf(startText) + startText.Length;
            var end = text.IndexOf("<script>");
            text = text.Substring(start, end - start);
            text = "<div" + text;
            text = text.Replace(@"</div><div>", string.Format("</div><div class=\"folder\" data-assembly=\"{0}\">", assemblyName));
            text = text.Replace("projectCS", "projectCSInSolution");
            text = text.Replace("projectVB", "projectVBInSolution");

            var projectInfoStart = text.IndexOf("<p class=\"projectInfo");
            if (projectInfoStart != -1)
            {
                var projectInfoEnd = text.IndexOf("</p>", projectInfoStart) + 4;
                if (projectInfoEnd != -1)
                {
                    text = text.Remove(projectInfoStart, projectInfoEnd - projectInfoStart);
                }
            }

            return text;
        }
    }
}
