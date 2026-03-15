using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Dto
{
    public class Rollup
    {
        public Children children { get; set; }
        public Summary summary { get; set; }
    }

    public class Summary
    {
        public string type { get; set; }
        public string content { get; set; }
        public string before { get; set; }
        public string keyword { get; set; }
        public string after { get; set; }
    }

    public class Children : Dictionary<string, string>
    {

    }

    public class PageProps
    {
        public SummaryParts summary { get; set; }
        public string youtubeUrl { get; set; }
    }

    public class Props
    {
        public PageProps pageProps { get; set; }
    }

    public class Query
    {
        public List<string> url { get; set; }
    }

    public class Rollups : Dictionary<string, Rollup>
    {

    }

    public class SummaryRoot
    {
        public Props props { get; set; }
        public string page { get; set; }
        public Query query { get; set; }
    }

    public class SummaryParts
    {
        public Rollups rollups { get; set; }
        public string title { get; set; }
    }

}
