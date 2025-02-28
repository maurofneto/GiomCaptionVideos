using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp1.Models
{
    internal class Video
    {
        public string Id { get; set; }
        public string Titulo { get; set; }
        public LegendaVideo Legenda { get; set; }
        public ResumoVideo Resumo { get; set; }
    }
}
