using Ryujinx.HLE.UI;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Headless
{
    /// <summary>
    /// Headless text processing class.
    /// </summary>
    internal class HeadlessDynamicTextInputHandler : IDynamicTextInputHandler
    {
        private bool _canProcessInput;

        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent { add { } remove { } }
        public event KeyReleasedHandler KeyReleasedEvent { add { } remove { } }

        public bool TextProcessingEnabled
        {
            get => Volatile.Read(ref _canProcessInput);

            set
            {
                Volatile.Write(ref _canProcessInput, value);

                // Launch a task to update the text.
                Task.Run(() =>
                {
                    Thread.Sleep(100);
                    TextChangedEvent?.Invoke("Ryu", 3, 3, false);
                });
            }
        }

        public HeadlessDynamicTextInputHandler()
        {
            _canProcessInput = false;
        }

        public void SetText(string text, int cursorBegin) { }

        public void SetText(string text, int cursorBegin, int cursorEnd) { }

        public void Dispose() { }
    }
}
