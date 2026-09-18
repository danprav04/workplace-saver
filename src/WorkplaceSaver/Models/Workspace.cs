using System;
using System.Collections.Generic;
using System.Linq;

namespace WorkplaceSaver.Models
{
    public class Workspace
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string? ThumbnailPath { get; set; }
        public string? Tags { get; set; }
        public int WindowCount { get; set; }

        public List<WindowSnapshot> Windows { get; set; } = new();

        public string CreatedAtFormatted => CreatedAt.ToString("MMM d, yyyy · h:mm tt");
        
        public string RelativeTime
        {
            get
            {
                var span = DateTime.Now - CreatedAt;
                if (span.TotalMinutes < 1) return "Just now";
                if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
                if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
                if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
                return CreatedAt.ToString("MMM d, yyyy");
            }
        }

        public string WindowCountSummary => WindowCount == 1 ? "1 app" : $"{WindowCount} apps";

        public IEnumerable<string> TagList => string.IsNullOrWhiteSpace(Tags) 
            ? Enumerable.Empty<string>() 
            : Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public IEnumerable<WindowSnapshot> DistinctAppIcons =>
            Windows.Where(w => !string.IsNullOrEmpty(w.AppIconBase64))
                   .GroupBy(w => w.ProcessName.ToLowerInvariant())
                   .Select(g => g.First())
                   .Take(6);
    }
}
