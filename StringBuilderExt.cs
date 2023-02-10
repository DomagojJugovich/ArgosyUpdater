using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArgosyUpdater
{
    public class StringBuilderExt 
    {
        private StringBuilder _sb;

        //constructor
        public StringBuilderExt() { 
            _sb = new StringBuilder();
        }

        //event
        public delegate void Notify(string line);
        public event Notify NotifyProgress; // event

        //property
        public int Length
        {
            get { return _sb.Length; }
            set {  }
        }

        //methods
        public StringBuilderExt AppendLine(string value)
        {
            _sb.AppendLine(value);
            NotifyProgress?.Invoke(value);
            return this;
        }

        public string ToString() { return _sb.ToString(); }
    }
}
