﻿using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace OmniSharp.AspNet5
{
    public class AspNet5Context
    {
        private int _contextId;

        public string RuntimePath { get; set; }
        
        public int DesignTimeHostPort { get; set; }

        public string HostId { get; private set; }

        public Dictionary<string, int> ProjectContextMapping { get; private set; }

        public Dictionary<int, Project> Projects { get; private set; }

        public Dictionary<ProjectId, FrameworkProject> WorkspaceMapping { get; private set; }

        public ProcessingQueue Connection { get; set; }

        public AspNet5Context()
        {
            HostId = Guid.NewGuid().ToString();
            ProjectContextMapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Projects = new Dictionary<int, Project>();
            WorkspaceMapping = new Dictionary<ProjectId, FrameworkProject>();
        }

        public bool TryAddProject(string projectFile, out Project project)
        {
            project = null;
            if (ProjectContextMapping.ContainsKey(projectFile))
            {
                return false;
            }

            var contextId = ++_contextId;

            // Create a mapping from path to contextid and back
            ProjectContextMapping[projectFile] = contextId;

            project = new Project
            {
                Path = projectFile,
                ContextId = contextId
            };

            Projects[contextId] = project;

            return true;
        }
    }
}