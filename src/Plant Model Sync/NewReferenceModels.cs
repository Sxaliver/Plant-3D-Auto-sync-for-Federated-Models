using System.Collections.Generic;

namespace IWMF.PlantModelSync
{
    internal sealed class ReferencePlacement
    {
        public string Source { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double Scale { get; set; }
        public double Rotation { get; set; }
    }
    internal sealed class AttachmentResult
    {
        public string Assembly { get; set; }
        public string Staged { get; set; }
        public string Backup { get; set; }
        public string BeforeHash { get; set; }
        public string AfterHash { get; set; }
        public List<string> Sources { get; set; } = new List<string>();
        public int Added { get; set; }
        public string State { get; set; } = "未执行";
    }
}
