using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FastMandelbrot {
    public partial class Form1 : Form {
        // The maximum and minimum number of iterations to test for membership in the set.
        const int MAX_ITERATIONS = 1000;
        const int MIN_ITERATIONS = 100;
        int _iterations;

        // Define the view (the section of the complex plane we are viewing).
        // Initialized to the view that nicely fits the Mandelbrot set.
        View _view = new View(-2, -1, 3, 2);
        
        // A persistent bitmap we will be drawing into and then drawing onto the canvas.
        // The canvas is the widget on the gui that displays the set to the user.
        Bitmap _pbm = null;

        // A Graphics object used to draw on _bm.
        // .NET uses a graphics object to encapsulate drawing actions.
        Graphics _g;

        // Used to scale and translate the view onto the canvas.
        double _scale = 1;
        double _xtran = 0;
        double _ytran = 0;

        // The color table to use.
        const int MAX_COLOR = 256;
        Color[] _cTable = new Color[MAX_COLOR];

        // The maximum number of threads to use.
        const int MAX_THREADS = 40;

        // The bailout value for the Mandelbrot test.
        const int MANDELBROT_BAILOUT = 4;

        // These items change every time the size of the output window changes, but because we may
        // redner the set multiple times without the window changing, we want to be able to persist 
        // these items between redraws.

        // The threads that will calculate the set.
        Thread[] t = new Thread[MAX_THREADS];

        // Each thread will render its part of the set onto its own bitmap... if they all drew onto 
        // the same bitmap, we would have to use locking to prevent corruption... and locking kills performance.
        Bitmap[] bm = new Bitmap[MAX_THREADS];

        // Each thread is responsible for a section of the output, startX and stopX define the range along
        // the x-axis that each thread is responsible for.
        int[] startX = new int[MAX_THREADS];
        int[] stopX = new int[MAX_THREADS];

        // Since each thread is only drawing a section of the overall set, we can define a rectangle that 
        // defines that section... this is usefull when drawing each thread's bitmap to the persistent bitmap...
        // we only need to copy the relevant section... if we copied each thread's entire bitmap to the persistent
        // bitmap, the machine would be doing much more work than is necessary.
        Rectangle[] r = new Rectangle[MAX_THREADS];

        // The diff between startX and stopX.
        int blocksize;

        // cr and ci cache the transformed complex number for each pixel in the output window.
        double[] ci;
        double[] cr;
                
        // Form constructor created by Visual Studio... when creating a gui-based app - either
        // WinForms or WPF - Visual studio creates a skeleton app in which it wires together
        // some basic parts of the application.
        public Form1 () {
            InitializeComponent();
        }

        
        // This is an event handler for the form's Load event.  By convention, event handlers
        // are typically named as objectName_eventName.  Event handlers must be bound to an
        // object's events - an event won't scan your code looking for a suitable handler, the
        // handler must be assigned (bound).  When using the gui builder, the gui builder
        // write the code that performs the bindings and place that code in a partial class
        // in another file named objectName.Designer.cs... so this form's events (and some
        // other things like instatiation of controls on the form) are coded up in Form1.Designer.cs.
        private void Form1_Load (object sender, EventArgs e) {
            loadColorTable(4);

            drawMandelbrot();
        }

        /// <summary>
        /// Load one of four color tables.
        /// 1. Semi-random with smooth transitions.
        /// 2. Alternating black and white.
        /// 3. Smooth continuous from black to white.
        /// 4. Smooth continuous from red to blue.
        /// </summary>
        /// <param name="selector">Selector.</param>
        private void loadColorTable(int selector){
            _cTable [0] = Color.Black;
            switch (selector) {
                case 1:
                    // all over the place with short runs of semi-smooth transitions
                    for (int i = 1; i < MAX_COLOR; i++) {
                        int r = (20 + i * 10) % 256;
                        int g = (30 + i * 20) % 256;
                        int b = (10 + i * 15) % 256;
                        _cTable [i] = Color.FromArgb (r, g, b);
                    }
                    break;

                case 2:
                    // repeated bands of black grey and white
                    for (int i = 1; i < MAX_COLOR; i++) {
                        _cTable [i] = Color.FromArgb (i % 3 * 122, i % 3 * 122, i % 3 * 122);
                    }
                    break;

                case 3:
                    // from black to white
                    for (int i = 1; i < MAX_COLOR; i++) {
                        _cTable [i] = Color.FromArgb (i * 255 / MAX_COLOR, i * 255 / MAX_COLOR, i * 255 / MAX_COLOR);
                    }
                    break;

                case 4:
                    // smooth transitions
                    for (int i = 1; i < MAX_COLOR; i++) {
                        double theta = 3 * Math.PI * i / MAX_COLOR;
                        _cTable [i] = Color.FromArgb ((int)(Math.Abs(Math.Sin(theta)) * 255), 0, (int)(Math.Abs(Math.Cos(theta)) * 255));
                    }
                    break;
            }
        }

        // event handler
        private void Form1_FormClosing (object sender, FormClosingEventArgs e) {
            disposeGraphics();
        }

        
        /// <summary>
        /// Create the persistent bitmap sized for the current size of the canvas, 
        /// the graphics object used to draw onto the bitmap, and the transforms.
        /// </summary>
        void getGraphicsAndTransforms () {
            disposeGraphics ();

            // Set the number of iterations based on how far we've zoomed in.
            // This provides sharper details when zooming in.
            _iterations = Math.Min(MAX_ITERATIONS, MIN_ITERATIONS + (int)(1 / _view.Width));

            // Create the persistent bitmap sized for the canvas and a Graphics object to draw onto it.
            _pbm = new Bitmap(canvas.Width, canvas.Height);
            _g = Graphics.FromImage(_pbm);

            double aspectCanvas = 1.0 * canvas.Width / canvas.Height;
            if (aspectCanvas < _view.Aspect) {
                // Fit view to window width.
                _scale = 1.0f * _view.Width / canvas.Width;
            }
            else {
                // Fit view to window height.
                _scale = 1.0f * _view.Height / canvas.Height;
            }

            // Center the view on the canvas.
            _xtran = -_view.Left / _scale + canvas.Width / 2 - _view.Width / 2 / _scale;
            _ytran = -_view.Top / _scale + canvas.Height / 2 - _view.Height / 2 / _scale;

            // Display transformed canvas size.
            statMessage.Text = String.Format("Left: {0}, Top {1}, Width: {2}, Height: {3}, Iter: {4}", -_xtran * _scale, -_ytran * _scale, canvas.Width * _scale, canvas.Height * _scale, _iterations);

            // To divide the x "columns" equally between the threads.
            blocksize = canvas.Width / MAX_THREADS;

            // Calculate and save the transformed x and y coordinates as cr and ci.
            ci = new double[canvas.Height];
            for (int y = 0; y < canvas.Height; y++) {
                ci[y] = (y - _ytran) * _scale;
            }

            cr = new double[canvas.Width];
            for (int x = 0; x < canvas.Width; x++) {
                cr[x] = (x - _xtran) * _scale;
            }

            // Set up per-thread items.
            for (int i = 0; i < MAX_THREADS; i++) {
                // Create a bitmap for this thread to draw into.  If all threads
                // were drawing into the same bitmap, we would have to use locks
                // to guarantee mutual exclusion and this would slow to a crawl.
                bm[i] = new Bitmap(canvas.Width, canvas.Height);

                // Each thread will work independantly on a number of columns from startX to stopX.
                startX[i] = i * blocksize;
                stopX[i] = (i + 1) * blocksize;

                // The rectangle defining the section "owned" by this thread.
                r[i] = new Rectangle (startX [i], 0, blocksize, canvas.Height);

                // If this is the last thread, make sure it "goes to the end."
                if (i == MAX_THREADS - 1) {
                    stopX[i] = canvas.Width;
                    r [i].Width = canvas.Width - startX [i];
                }
            }

        }

        
        /// <summary>
        /// Dispose assets.
        /// </summary>
        void disposeGraphics () {
            if (_g != null) {
                _g.Dispose();
            }

            if (_pbm != null) {
                _pbm.Dispose();
            }

            for (int i = 0; i < MAX_THREADS; i++) {
                if (bm [i] != null) {
                    bm [i].Dispose ();
                }
            }
        }


        /// <summary>
        /// Non-optimized routine to draw the intermediate results. Trigger it by hitting "D".
        /// </summary>
        void dotIterations () {
            getGraphicsAndTransforms();

            double zr = 0, zi = 0;

            for (int x = 0; x < canvas.Width; x++) {
                _g.Clear(Color.Black); 

                for (int y = 0; y < canvas.Height; y++) {
                    // to plot calcs for individual points, uncomment the following
                    //_g.Clear(Color.Black); 

                    double cr = (x - _xtran) * _scale;
                    double ci = (y - _ytran) * _scale;
                    double znr = cr;
                    double zni = ci;
                    
                    int rgb = 0;
                    while (++rgb < 10000) {
                        zr = znr * znr;
                        zi = zni * zni;
                        if (zr + zi > 4) {
                            break;
                        }

                        zr = cr + zr - zi;
                        zi = (znr * zni);
                        zi += zi + ci;

                        znr = zr;
                        zni = zi;

                        int zx = (int) (_xtran + zr / _scale);
                        int zy = (int) (_ytran + zi / _scale);
                        if (zx >= 0 && zx < canvas.Width && zy >= 0 && zy < canvas.Height) {
                            _pbm.SetPixel(zx, zy, _cTable[rgb % _iterations % MAX_COLOR]);
                            //_bm.SetPixel(zx, zy, Color.White);
                        }
                    }

                    // to plot calcs for individual points uncomment the following
                    /*
                    if (zr + zi <= 4) {
                        PaintView();
                    }
                    //*/
                }

                PaintView();
            }
        }

        /// <summary>
        /// Draw the current view of the Mandelbrot set onto the persistent bitmap.
        /// </summary>
        void drawMandelbrot () {
            for (int i = 0; i < MAX_THREADS; i++) {
                // The code to be run by each thread is defined in-line, below as
                // a lambda.  The lambda forms a closure on all variables that are 
                // in scope including the loop-counter i.  But an individual thread
                // should not use i as a "my index" value because all threads "see"
                // the same i. To give each thread a separate value for the 
                // thread-counter, we need a variable that is scoped inside the loop.
                // By doing this, each iteration of the loop (and therefore each thread)
                // has a unique index value.
                int localI = i;

                // Create a thread.
                t[i] = new Thread(() => {
                    int iCount = 0;
                    double zr;
                    double zi;

                    // Mandelbrot "formula" is:
                    //   z[n+1] = z[n] ^ 2 + c
                    // where:
                    //   c is the complex number (x + yi)
                    //   z[0] = c
                    // I'm using zr and zi for the real and imaginary
                    // parts of z[n], and zrNext and ziNext for the
                    // real and imaginary parts of z[n+1], and cr and ci[y]
                    // for the real and imaginary parts of c.
                            
                    // Allocate z[n+1]
                    double zrNext, ziNext;

                    // For each x in this thread's section...
                    for (int x = startX[localI]; x < stopX[localI]; x++) {
                        // For each y in this column...
                        for (int y = 0; y < canvas.Height; y++) {
                            // Allocate and assign z[0] = c
                            // Note we are using the cached, scaled x and y that were
                            // calculated in getGraphicsAndTransforms().
                            zr = cr[x];
                            zi = ci[y];
                            
                            iCount = 0;
                            while (++iCount < _iterations) {
                                // The squares will dominate the answer, so our bailout
                                // only needs to check them.
                                zrNext = zr * zr;
                                ziNext = zi * zi;

                                if (zrNext + ziNext > MANDELBROT_BAILOUT) {
                                    break;
                                }

                                // Finish the mandelbrot formula.
                                zi = 2 * zr * zi + ci[y];
                                zr = zrNext - ziNext + cr[x];
                            }

                            // Finally, set the current pixel using a color based on
                            // when the Mandelbrot loop terminated.
                            bm[localI].SetPixel(x, y, _cTable[iCount % _iterations % MAX_COLOR]);
                        }
                    }
                });

                // And now start the thread we just defined.
                t[i].Start();
            }

            // After all threads have been launched,
            for (int i = 0; i < MAX_THREADS; i++) {
                // Wait for each thread to terminate.
                t[i].Join();

                // Draw this thread's bitmap onto the persistent bitmap.
                _g.DrawImage(bm[i], r[i], r[i], GraphicsUnit.Pixel);
            }

            // Now that all threads have drawn their section to the persistent bitmap,
            // paint it onto the canvas.
            PaintView();
        }


        // Don't know yet if System.Drawing has a way to cycle the color palette.
        // Here's a hack that cycles the colors in the color table and then - in 
        // a very brute-force way - simply recalcs and redraws the set with the new color table.
        // Even if System.Drawing doesn't supply palette animation, we could probably
        // read each pixel's color from the bitmap and reset it to the next color.  This would
        // be a lot faster than what is currently being done.
        private void colorCycle () {
            for (int j = 0; j < MAX_COLOR; j++) {
                Color c = _cTable [1];
                for (int i = 1; i < MAX_COLOR - 1; i++) {
                    _cTable [i] = _cTable [i + 1];
                }
                _cTable [MAX_COLOR - 1] = c;

                drawMandelbrot ();
            }
        }
        
        // Event handler for keypresses.
        private void Form1_KeyUp (object sender, KeyEventArgs e) {
            switch (e.KeyCode) {
                case Keys.Add:
                    // zoom in
                    _view.Zoom (0.2f);
                    getGraphicsAndTransforms ();
                    drawMandelbrot ();
                    break;

                case Keys.Subtract:
                    // zoom out
                    _view.Zoom (-0.2f);
                    getGraphicsAndTransforms ();
                    drawMandelbrot ();
                    break;

                case Keys.Left:
                    // pan left
                    _view.Pan (-0.2f, 0);
                    getGraphicsAndTransforms ();
                    drawMandelbrot ();
                    break;

                case Keys.Right:
                    // pan right
                    _view.Pan (0.2f, 0);
                    getGraphicsAndTransforms ();
                    drawMandelbrot ();
                    break;

                case Keys.Up:
                    // pan up
                    _view.Pan (0, -0.2f);
                    getGraphicsAndTransforms ();
                    drawMandelbrot ();
                    break;

                case Keys.Down:
                    // pan down
                    _view.Pan (0, 0.2f);
                    getGraphicsAndTransforms ();
                    drawMandelbrot ();
                    break;

                case Keys.Home:
                    // reset view to intial coordinates
                    _view.Reset ();
                    getGraphicsAndTransforms ();
                    drawMandelbrot ();
                    break;

                case Keys.C:
                    // cycle the color table
                    colorCycle ();
                    break;

                case Keys.D:
                    // draw intermediate calculation results
                    dotIterations ();
                    break;

                case Keys.D1: // D1 is the "1" key
                    // select color table 1
                    loadColorTable (1);
                    drawMandelbrot ();
                    break;

                case Keys.D2: // D2 is the "2" key, etc.
                    // select color table 2
                    loadColorTable (2);
                    drawMandelbrot ();
                    break;

                case Keys.D3:
                    // select color table 3
                    loadColorTable (3);
                    drawMandelbrot ();
                    break;

                case Keys.D4:
                    // select color table 4
                    loadColorTable (4);
                    drawMandelbrot ();
                    break;
            }
        }

        // Event handler for window resize.
        private void canvas_Resize (object sender, EventArgs e) {
            getGraphicsAndTransforms();
            drawMandelbrot();
        }

        #region zoom-in-box
        // Track the "drawing" of a rectange for zooming with the mouse.
        int zoomX = 0;
        int zoomY = 0;
        bool mouseDown = false;
        
        // Event handler for the when the mouse button is pressed.
        private void canvas_MouseDown (object sender, MouseEventArgs e) {
            zoomX = e.X;
            zoomY = e.Y;
            mouseDown = true;
        }

        // Event handler for mouse move.
        private void canvas_MouseMove (object sender, MouseEventArgs e) {
            if (mouseDown) {
                // Repaint the current mandelbrot onto the canvas to erase any previous sizing box.
                PaintView();

                // Now draw a sizing box using the mouse-down coordinates and the current
                // mouse coordinates.
                Graphics cg = canvas.CreateGraphics();
                cg.DrawRectangle(Pens.Red, Math.Min(zoomX, e.X), Math.Min(zoomY, e.Y), Math.Abs(e.X - zoomX), Math.Abs(e.Y - zoomY));
                cg.Dispose();

                // Display transformed new viewport size.
                statMessage.Text = String.Format("Left: {0}, Top {1}, Width: {2}, Height: {3}", (Math.Min(zoomX, e.X) - _xtran) * _scale, (Math.Min(zoomY, e.Y) - _ytran) * _scale, Math.Abs(e.X - zoomX) * _scale, Math.Abs(e.Y - zoomY) * _scale);
            }
        }

        // Event handler for mouse-up.
        private void canvas_MouseUp (object sender, MouseEventArgs e) {
            mouseDown = false;

            // Set our new view to the translated coordinates of the sizing box.
            _view.Left = (Math.Min(zoomX, e.X) - _xtran) * _scale;
            _view.Top = (Math.Min(zoomY, e.Y) - _ytran) * _scale;
            _view.Width = Math.Abs(e.X - zoomX) * _scale;
            _view.Height = Math.Abs(e.Y - zoomY) * _scale;

            getGraphicsAndTransforms ();
            // And redraw the new view.
            drawMandelbrot();
        }

        #endregion zoom-in-box

        // Event handler for paint event (e.g. when your window is uncovered, windows informs
        // you that your window needs to be updated.
        private void canvas_Paint (object sender, PaintEventArgs e) {
            PaintView();
        }

        /// <summary>
        /// Draw the persistent Mandelbrot bitmap to the canvas.
        /// </summary>
        private void PaintView () {
            Graphics cg = canvas.CreateGraphics();
            cg.DrawImage(_pbm, 0, 0);
            cg.Dispose();
        }
    }

    /// <summary>
    /// Encapsulate viewport properties.
    /// </summary>
    struct View {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
        
        // Private members save original values for Reset.
        private double _left;
        private double _top;
        private double _width;
        private double _height;

        public View (double left, double top, double width, double height) {
            _left = left;
            _top = top;
            _width = width;
            _height = height;

            Left = _left;
            Top = _top;
            Width = _width;
            Height = _height;
        }

        public void Reset () {
            Left = _left;
            Top = _top;
            Width = _width;
            Height = _height;
        }

        public double Aspect {
            get {
                return Width / Height;
            }
        }

        public void Zoom (double percent) {
            double xDelta = Width * percent;
            double yDelta = Height * percent;

            Left += xDelta / 2;
            Width -= xDelta;
            Top += yDelta / 2;
            Height -= yDelta;
        }

        public void Pan (double xPercent, double yPercent) {
            double xDelta = Width * xPercent;
            double yDelta = Height * yPercent;

            Left += xDelta;
            Top += yDelta;
        }
    }
}
