using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            _sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + value);
            RaiseNotify(value);
            return this;
        }

        //display only: a failing subscriber (e.g. closed progress window) must not break the sync that is logging,
        //it is unsubscribed and noted once in the log itself so it stays visible
        private void RaiseNotify(string value)
        {
            var handlers = NotifyProgress;
            if (handlers == null) return;

            foreach (Notify handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(value);
                }
                catch (Exception ex)
                {
                    NotifyProgress -= handler;
                    _sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + "PROGRESS DISPLAY FAILED, DETACHED : " + ex.GetType().Name + " " + ex.Message);
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
        }

        public void Append(string txt)
        {
            _sb.Append(txt);
        }

        public string ToString() { return _sb.ToString(); }
    }
}
