using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    public class CapituloResumoVideo
    {
        public int Id { get; set; }
        public int ResumoVideoId { get; set; }
        public string Resumo { get; set; }
        public TimeSpan Inicio { get; set; }
    }
}
