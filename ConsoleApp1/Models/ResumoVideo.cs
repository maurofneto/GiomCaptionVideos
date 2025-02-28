using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp1.Models
{
    internal class ResumoVideo
    {
        public int Id { get; set; }

        public string Descricao { get; set; }
        public string VideoId { get; set; }
        public CapituloResumoVideo[] Capitulos { get; set; }
    }
}
