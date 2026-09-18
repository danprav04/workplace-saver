using System;

namespace WorkplaceSaver.Models
{
    public class WindowSnapshot
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string WorkspaceId { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string? CommandLine { get; set; }
        public string WindowTitle { get; set; } = string.Empty;
        public string? ClassName { get; set; }
        
        // Window Placement
        public int ShowCmd { get; set; } = 1; // 1 = Normal, 2 = Minimized, 3 = Maximized
        public int Flags { get; set; }
        public int NormalLeft { get; set; }
        public int NormalTop { get; set; }
        public int NormalRight { get; set; }
        public int NormalBottom { get; set; }
        public int ZOrder { get; set; }

        // Cached Base64 representation of app icon (32x32 PNG)
        public string? AppIconBase64 { get; set; }

        public int Width => Math.Max(0, NormalRight - NormalLeft);
        public int Height => Math.Max(0, NormalBottom - NormalTop);

        public string DisplayName => !string.IsNullOrWhiteSpace(WindowTitle) 
            ? WindowTitle 
            : (!string.IsNullOrWhiteSpace(ProcessName) ? ProcessName : "Application");

        public string StateDescription => ShowCmd switch
        {
            2 => "Minimized",
            3 => "Maximized",
            _ => "Normal"
        };
    }
}
