using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    public class Video
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public LegendaVideo? Legenda { get; set; }
        public ResumoVideo? Resumo { get; set; }
        public DateTime? UploadedAt { get; set; }
        public string PrivacyStatus { get; set; }
        public string PlaylistId { get; set; }
        public string ChannelId { get; set; }
        public string ChannelTitle { get; set; }
        public string Description { get; set; }
        public string Length { get; set; }
    }
}
