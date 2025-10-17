using System;
using System.Drawing;
using System.Windows.Forms;

namespace SWA.UI.Common
{
    /// <summary>
    /// Smart window state manager that handles minimize/restore WITHOUT reloading content
    /// Preserves exact window size and position on restore
    /// </summary>
    public class WindowStateManager
    {
        private readonly Form form;
        private FormWindowState previousState;
        private FormWindowState stateBeforeMinimize;
        private Size sizeBeforeMinimize;
        private Point locationBeforeMinimize;
        private bool isMinimized;
        private bool isInitialized;

        public WindowStateManager(Form form)
        {
            this.form = form ?? throw new ArgumentNullException(nameof(form));
            this.previousState = form.WindowState;
            this.stateBeforeMinimize = form.WindowState;
            this.sizeBeforeMinimize = form.Size;
            this.locationBeforeMinimize = form.Location;
            this.isMinimized = false;
            this.isInitialized = false;

            // Subscribe to form events
            form.Resize += OnFormResize;
            form.SizeChanged += OnFormSizeChanged;
            form.Shown += OnFormShown;
        }

        private void OnFormShown(object sender, EventArgs e)
        {
            // Mark as initialized after form is shown
            isInitialized = true;
        }

        /// <summary>
        /// Event raised when window is being minimized
        /// </summary>
        public event EventHandler Minimizing;

        /// <summary>
        /// Event raised when window is being restored from minimized state
        /// </summary>
        public event EventHandler Restoring;

        /// <summary>
        /// Event raised after window is fully restored
        /// </summary>
        public event EventHandler Restored;

        private void OnFormResize(object sender, EventArgs e)
        {
            // Don't process events until form is fully initialized
            if (!isInitialized)
            {
                previousState = form.WindowState;
                return;
            }

            FormWindowState currentState = form.WindowState;

            // Detect minimization - save current size and position BEFORE minimizing
            if (currentState == FormWindowState.Minimized && previousState != FormWindowState.Minimized)
            {
                // Save the state before minimizing
                stateBeforeMinimize = previousState;
                sizeBeforeMinimize = form.Size;
                locationBeforeMinimize = form.Location;

                isMinimized = true;
                Minimizing?.Invoke(this, EventArgs.Empty);
            }
            // Detect restoration from minimize - restore EXACT size and position
            else if (currentState != FormWindowState.Minimized && previousState == FormWindowState.Minimized && isMinimized)
            {
                isMinimized = false;
                Restoring?.Invoke(this, EventArgs.Empty);

                // Restore the exact state after a small delay to ensure proper restoration
                form.BeginInvoke(new Action(() =>
                {
                    // Restore exact window state, size, and position
                    form.WindowState = stateBeforeMinimize;
                    form.Size = sizeBeforeMinimize;
                    form.Location = locationBeforeMinimize;

                    Restored?.Invoke(this, EventArgs.Empty);
                }));
            }

            previousState = currentState;
        }

        private void OnFormSizeChanged(object sender, EventArgs e)
        {
            // Don't process events until form is fully initialized
            if (!isInitialized)
                return;

            // Additional check for size changes
            if (form.WindowState == FormWindowState.Minimized && !isMinimized)
            {
                isMinimized = true;
                Minimizing?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Minimize the window
        /// </summary>
        public void Minimize()
        {
            if (form.WindowState != FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Minimized;
            }
        }

        /// <summary>
        /// Restore the window to normal state
        /// </summary>
        public void Restore()
        {
            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }
        }

        /// <summary>
        /// Check if window is currently minimized
        /// </summary>
        public bool IsMinimized => isMinimized;

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Dispose()
        {
            if (form != null)
            {
                form.Resize -= OnFormResize;
                form.SizeChanged -= OnFormSizeChanged;
                form.Shown -= OnFormShown;
            }
        }
    }
}
