using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BaobaoDesktop
{
    /// <summary>
    /// Original-pixel idle renderer. Only the two ears and the curled tail are
    /// articulated; the current animation Source supplies all other pixels.
    /// The clean plate is used solely below an actively moving ear's mask.
    /// The terminal tail and surrounding transparent floor share one continuous
    /// inverse deformation; no generated plate pixels enter that region.
    /// All geometry below uses the approved 362 x 362 frame00 coordinates.
    /// </summary>
    public sealed class IdlePetRenderer : FrameworkElement
    {
        private const double DesignSize = 362.0;
        private const byte AlphaCutoff = 12;
        private const double PetHeadDegrees=6.0;
        private const double PetHeadShiftX=9.0;
        private const double PetHeadShiftY=1.2;
        private BitmapSource source;
        private BitmapSource neutralSource;
        private BitmapSource cleanPlate;
        private IdleMotionPose pose;
        private bool layeredIdle;
        private bool petting;
        private double petBlend,petSway,petBreath;
        private BitmapSource petClosedSource;
        private Raster petClosedRaster;
        private byte[] petClosedPixels,petWorkingPixels,petEyeMask;
        private int petWidth,petHeight;
        private bool dirty = true;
        private readonly Dictionary<BitmapSource, Raster> sourceCache = new Dictionary<BitmapSource, Raster>();
        private Raster neutralRaster;
        private Raster cleanRaster;
        private byte[] neutralPixels;
        private byte[] cleanPixels;
        private bool cleanContentValid;
        private byte[] leftMask;
        private byte[] rightMask;
        private int maskWidth;
        private int maskHeight;
        private byte[] outputPixels;
        private BitmapSource displayedBitmap;
        private byte[] displayedPixels;
        private int displayedWidth;
        private int displayedHeight;
        private WriteableBitmap animatedBitmap;

        private static readonly Point[] LeftEarOutline = new Point[] {
            new Point(102,91), new Point(100,71), new Point(100,49),
            new Point(103,32), new Point(109,23), new Point(117,22),
            new Point(129,32), new Point(143,52), new Point(154,72)
        };
        private static readonly Point[] RightEarOutline = new Point[] {
            new Point(173,78), new Point(181,64), new Point(199,49),
            new Point(217,40), new Point(226,41), new Point(231,50),
            new Point(227,70), new Point(222,90), new Point(219,108)
        };
        private static readonly Point LeftEarRoot = new Point(127,82);
        private static readonly Point RightEarRoot = new Point(198,94);

        public BitmapSource Source
        {
            get { return source; }
            set { if (!Object.ReferenceEquals(source,value)) { source=value; MarkDirty(); InvalidateMeasure(); } }
        }
        public BitmapSource NeutralSource
        {
            get { return neutralSource; }
            set
            {
                if (Object.ReferenceEquals(neutralSource,value)) return;
                neutralSource=value; neutralRaster=null; neutralPixels=null; petClosedPixels=null;petWorkingPixels=null;petEyeMask=null; MarkDirty();
            }
        }
        public BitmapSource CleanPlate
        {
            get { return cleanPlate; }
            set
            {
                if (Object.ReferenceEquals(cleanPlate,value)) return;
                cleanPlate=value; cleanRaster=null; cleanPixels=null; MarkDirty();
            }
        }
        public IdleMotionPose Pose
        {
            get { return pose; }
            set
            {
                if (pose.LeftEarDegrees == value.LeftEarDegrees && pose.RightEarDegrees == value.RightEarDegrees &&
                    pose.TailLift == value.TailLift && pose.TailSway == value.TailSway) return;
                pose=value; MarkDirty();
            }
        }
        public bool LayeredIdle
        {
            get { return layeredIdle; }
            set { if (layeredIdle != value) { layeredIdle=value; MarkDirty(); } }
        }
        public bool Petting
        {
            get{return petting;}
            set{if(petting!=value){petting=value;MarkDirty();}}
        }
        public double PetBlend
        {
            get{return petBlend;}
            set{double next=FiniteClamp(value,0,1);if(petBlend!=next){petBlend=next;MarkDirty();}}
        }
        public double PetSway
        {
            get{return petSway;}
            set{double next=FiniteClamp(value,-1,1);if(petSway!=next){petSway=next;MarkDirty();}}
        }
        public double PetBreath
        {
            get{return petBreath;}
            set{double next=FiniteClamp(value,0,1);if(petBreath!=next){petBreath=next;MarkDirty();}}
        }
        public BitmapSource PetClosedSource
        {
            get{return petClosedSource;}
            set{if(!Object.ReferenceEquals(petClosedSource,value)){petClosedSource=value;petClosedRaster=null;petClosedPixels=null;MarkDirty();}}
        }

        private void MarkDirty() { dirty=true; InvalidateVisual(); }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = source == null ? DesignSize : source.PixelWidth;
            double h = source == null ? DesignSize : source.PixelHeight;
            return new Size(Double.IsInfinity(availableSize.Width) ? w : Math.Min(w,availableSize.Width),
                Double.IsInfinity(availableSize.Height) ? h : Math.Min(h,availableSize.Height));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            EnsureFrame();
            if (displayedBitmap != null && RenderSize.Width > 0 && RenderSize.Height > 0)
                drawingContext.DrawImage(displayedBitmap,new Rect(0,0,RenderSize.Width,RenderSize.Height));
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            EnsureFrame();
            Point p = hitTestParameters.HitPoint;
            if (displayedPixels == null || RenderSize.Width <= 0 || RenderSize.Height <= 0 ||
                p.X < 0 || p.Y < 0 || p.X >= RenderSize.Width || p.Y >= RenderSize.Height) return null;
            int x = Math.Min(displayedWidth-1,(int)(p.X / RenderSize.Width * displayedWidth));
            int y = Math.Min(displayedHeight-1,(int)(p.Y / RenderSize.Height * displayedHeight));
            return displayedPixels[(y*displayedWidth+x)*4+3] >= AlphaCutoff ? new PointHitTestResult(this,p) : null;
        }

        private void EnsureFrame()
        {
            if (!dirty) return;
            dirty=false;
            bool renderPet=petting||petBlend>0.0001||Math.Abs(petSway)>0.0001||petBreath>0.0001;
            BitmapSource selected=renderPet&&neutralSource!=null?neutralSource:source;
            if (selected == null) { displayedBitmap=null; displayedPixels=null; return; }
            Raster body;
            if (!sourceCache.TryGetValue(selected,out body))
            {
                body = ReadRaster(selected);
                if (sourceCache.Count >= 64) sourceCache.Clear();
                sourceCache.Add(selected,body);
            }
            displayedWidth=body.Width; displayedHeight=body.Height;
            displayedBitmap=body.Bitmap; displayedPixels=body.Pixels;
            if(renderPet)
            {
                // Petting owns a single frame00 canvas through entry AND return.
                // Legacy Source20..23 changes cannot leak head/body jumps here.
                if(petBlend==0&&petSway==0&&petBreath==0&&ZeroPose())return;
                RenderPet(body);
                return;
            }

            if(!layeredIdle)return;
            try
            {
                bool changed=ComposeEars(body);
                if(TailMoving())
                {
                    if(!changed)PrepareOutput(body);
                    RenderTail(body.Pixels,body.Width,body.Height);
                    changed=true;
                }
                if(changed)PublishPixels(body.Width,body.Height);
            }
            catch (ArgumentException) { displayedBitmap=body.Bitmap; displayedPixels=body.Pixels; }
            catch (InvalidOperationException) { displayedBitmap=body.Bitmap; displayedPixels=body.Pixels; }
        }

        private bool ZeroPose()
        {
            return FiniteClamp(pose.LeftEarDegrees,-6,6)==0&&FiniteClamp(pose.RightEarDegrees,-6,6)==0&&
                FiniteClamp(pose.TailLift,0,1)==0&&FiniteClamp(pose.TailSway,-1,1)==0;
        }

        // Used before the Pet head warp as well as by ordinary idle rendering.
        // Thus a locally articulated ear subsequently follows the moving head.
        private bool ComposeEars(Raster body)
        {
            double left=FiniteClamp(pose.LeftEarDegrees,-6,6);
            double right=FiniteClamp(pose.RightEarDegrees,-6,6);
            bool moveLeft=Math.Abs(left)>0.0001, moveRight=Math.Abs(right)>0.0001;
            if ((!moveLeft && !moveRight) || neutralSource == null || cleanPlate == null ||
                !MatchingAspect(body,neutralSource) || !MatchingAspect(body,cleanPlate)) return false;
                EnsureLayers(body.Width,body.Height);
                if (!cleanContentValid) return false;
                PrepareOutput(body);
                // Keep each inactive part byte-identical to the current Source.
                // In particular, tail poses cannot leak clean-plate face pixels.
                for (int i=0;i<body.Width*body.Height;i++)
                {
                    int mask=0;
                    if (moveLeft) mask=Math.Max(mask,leftMask[i]);
                    if (moveRight) mask=Math.Max(mask,rightMask[i]);
                    if (mask == 0) continue;
                    int p=i*4;
                    if (mask == 255)
                    {
                        outputPixels[p]=cleanPixels[p]; outputPixels[p+1]=cleanPixels[p+1];
                        outputPixels[p+2]=cleanPixels[p+2]; outputPixels[p+3]=cleanPixels[p+3];
                    }
                    else
                        for (int c=0;c<4;c++) outputPixels[p+c]=(byte)((body.Pixels[p+c]*(255-mask)+cleanPixels[p+c]*mask+127)/255);
                }
                if (moveLeft) DrawEar(body.Width,body.Height,leftMask,LeftEarOutline,LeftEarRoot,left);
                if (moveRight) DrawEar(body.Width,body.Height,rightMask,RightEarOutline,RightEarRoot,right);
                return true;
        }

        private void PrepareOutput(Raster body)
        {
            if(outputPixels==null||outputPixels.Length!=body.Pixels.Length)outputPixels=new byte[body.Pixels.Length];
            Buffer.BlockCopy(body.Pixels,0,outputPixels,0,body.Pixels.Length);
        }

        private bool TailMoving()
        {return FiniteClamp(pose.TailLift,0,1)!=0||FiniteClamp(pose.TailSway,-1,1)!=0;}

        private void RenderPet(Raster body)
        {
            int width=body.Width,height=body.Height;
            if(petWorkingPixels==null||petWidth!=width||petHeight!=height||petEyeMask==null)
            {
                petWidth=width;petHeight=height;petWorkingPixels=new byte[body.Pixels.Length];petEyeMask=new byte[width*height];petClosedPixels=null;
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {
                    double px=(x+0.5)*DesignSize/width,py=(y+0.5)*DesignSize/height;
                    double mask=Math.Max(EyeWeight(px,py,138,110,17,10),EyeWeight(px,py,184,117,18,11));
                    petEyeMask[y*width+x]=(byte)Math.Round(mask*255);
                }
            }
            if(petClosedPixels==null&&petClosedSource!=null&&MatchingAspect(body,petClosedSource))
            {
                if(petClosedRaster==null)petClosedRaster=ReadRaster(petClosedSource);
                petClosedPixels=Resample(petClosedRaster,width,height);
            }
            byte[] basePixels=body.Pixels;
            try {if(ComposeEars(body))basePixels=outputPixels;}
            catch(ArgumentException) { }
            catch(InvalidOperationException) { }
            // Only the ear layer differences are faded. Their clean-plate
            // seams must also disappear smoothly at a part's own zero pose,
            // which can occur in the middle of a long Pet interaction.
            if(!Object.ReferenceEquals(basePixels,body.Pixels))
            {
                double leftCover=Smooth01(Math.Abs(FiniteClamp(pose.LeftEarDegrees,-6,6))/.75);
                double rightCover=Smooth01(Math.Abs(FiniteClamp(pose.RightEarDegrees,-6,6))/.75);
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {
                    double cover=petBlend*(x*DesignSize/width<170?leftCover:rightCover);
                    int pixel=(y*width+x)*4;
                    for(int c=0;c<4;c++)petWorkingPixels[pixel+c]=Byte(body.Pixels[pixel+c]*(1-cover)+basePixels[pixel+c]*cover);
                }
            }
            else Buffer.BlockCopy(basePixels,0,petWorkingPixels,0,body.Pixels.Length);
            if(petClosedPixels!=null&&petBlend>0)
                for(int i=0;i<petEyeMask.Length;i++)
                {
                    double amount=petEyeMask[i]/255.0*petBlend;if(amount<=0)continue;int pixel=i*4;
                    for(int c=0;c<4;c++)petWorkingPixels[pixel+c]=Byte(petWorkingPixels[pixel+c]*(1-amount)+petClosedPixels[pixel+c]*amount);
                }
            if(outputPixels==null||outputPixels.Length!=body.Pixels.Length)outputPixels=new byte[body.Pixels.Length];
            Buffer.BlockCopy(petWorkingPixels,0,outputPixels,0,outputPixels.Length);
            if(Math.Abs(petSway)>0.0001||petBreath>0.0001)
            {
                double angle=petSway*PetHeadDegrees*Math.PI/180,cos=Math.Cos(angle),sin=Math.Sin(angle);
                double tx=petSway*PetHeadShiftX,ty=petSway*PetHeadShiftY;
                int x0=Math.Max(0,(int)(25*width/DesignSize)),x1=Math.Min(width-1,(int)Math.Ceiling(310*width/DesignSize));
                int y0=0,y1=Math.Min(height-1,(int)Math.Ceiling(234*height/DesignSize));
                for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
                {
                    double dx=(x+0.5)*DesignSize/width,dy=(y+0.5)*DesignSize/height;
                    if(HeadWeight(dx,dy)<=0&&BreathWeight(dx,dy)<=0)continue;
                    Point origin=InversePetDetailPoint(dx,dy,cos,sin,tx,ty,petBreath);
                    Sample sample=SamplePixel(petWorkingPixels,null,width,height,origin.X*width/DesignSize-.5,origin.Y*height/DesignSize-.5);
                    int pixel=(y*width+x)*4;
                    outputPixels[pixel]=Byte(sample.B);outputPixels[pixel+1]=Byte(sample.G);outputPixels[pixel+2]=Byte(sample.R);outputPixels[pixel+3]=Byte(sample.A);
                }
            }
            RenderTail(petWorkingPixels,width,height);
            PublishPixels(width,height);
        }

        // The terminal tail bends out over the floor, rather than being lifted
        // into a same-colored clean-plate abdomen. Warp original tail AND its
        // transparent surroundings together; no replacement body patch or
        // second copy of the tail is composited here.
        private void RenderTail(byte[] original,int width,int height)
        {
            double lift=FiniteClamp(pose.TailLift,0,1),sway=FiniteClamp(pose.TailSway,-1,1);
            if(lift==0&&sway==0)return;
            double tx=-1.5*lift+1.2*sway,ty=10*lift;
            int x0=Math.Max(0,(int)(196*width/DesignSize)),x1=Math.Min(width-1,(int)Math.Ceiling(305*width/DesignSize));
            int y0=Math.Max(0,(int)(282*height/DesignSize)),y1=Math.Min(height-1,(int)Math.Ceiling(360*height/DesignSize));
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
            {
                double dx=(x+.5)*DesignSize/width,dy=(y+.5)*DesignSize/height;
                if(PetTailWeight(dx,dy)<=0)continue;
                Point origin=InversePetTailPoint(dx,dy,tx,ty);
                Sample sample=SamplePixel(original,null,width,height,origin.X*width/DesignSize-.5,origin.Y*height/DesignSize-.5);
                int pixel=(y*width+x)*4;
                outputPixels[pixel]=Byte(sample.B);outputPixels[pixel+1]=Byte(sample.G);outputPixels[pixel+2]=Byte(sample.R);outputPixels[pixel+3]=Byte(sample.A);
            }
        }

        private static Point InversePetTailPoint(double dx,double dy,double tx,double ty)
        {
            double sx=dx,sy=dy;
            for(int i=0;i<20;i++)
            {
                double weight=PetTailWeight(sx,sy),nx=dx-tx*weight,ny=dy-ty*weight;
                if(i>=5&&Math.Abs(nx-sx)+Math.Abs(ny-sy)<.005)break;
                sx=nx;sy=ny;
            }
            return new Point(sx,sy);
        }

        private static double PetTailWeight(double x,double y)
        {
            return Smooth01((x-196)/16.0)*(1-Smooth01((x-232)/70.0))*
                Smooth01((y-283)/21.0)*(1-Smooth01((y-340)/24.0));
        }

        private void PublishPixels(int width,int height)
        {
            for(int i=0;i<outputPixels.Length;i+=4)if(outputPixels[i+3]<AlphaCutoff)outputPixels[i]=outputPixels[i+1]=outputPixels[i+2]=outputPixels[i+3]=0;
            if(animatedBitmap==null||animatedBitmap.PixelWidth!=width||animatedBitmap.PixelHeight!=height)
                animatedBitmap=new WriteableBitmap(width,height,96,96,PixelFormats.Pbgra32,null);
            animatedBitmap.WritePixels(new Int32Rect(0,0,width,height),outputPixels,width*4,0);
            displayedBitmap=animatedBitmap;displayedPixels=outputPixels;
        }

        private static Point InversePetPoint(double dx,double dy,double cos,double sin,double tx,double ty)
        {return InversePetDetailPoint(dx,dy,cos,sin,tx,ty,0);}

        private static Point InversePetDetailPoint(double dx,double dy,double cos,double sin,double tx,double ty,double breath)
        {
            double sx=dx,sy=dy;
            for(int iteration=0;iteration<16;iteration++)
            {
                double amount=HeadWeight(sx,sy),rx=sx-158,ry=sy-153;
                double moveX=cos*rx-sin*ry-rx+tx,moveY=sin*rx+cos*ry-ry+ty;
                double nextX=dx-amount*moveX,nextY=dy-amount*moveY+breath*.8*BreathWeight(sx,sy);
                if(iteration>=5&&Math.Abs(nextX-sx)+Math.Abs(nextY-sy)<.01)break;
                sx=nextX;sy=nextY;
            }
            return new Point(sx,sy);
        }

        private static double BreathWeight(double x,double y)
        {
            double dx=(x-156)/46.0,dy=(y-195)/37.0,radius=dx*dx+dy*dy;
            if(radius>=1)return 0;
            double amount=1-radius;return amount*amount;
        }

        private static double EyeWeight(double x,double y,double cx,double cy,double rx,double ry)
        {
            double dx=(x-cx)/rx,dy=(y-cy)/ry;
            double radius=Math.Sqrt(dx*dx+dy*dy);
            return 1-Smooth01((radius-.68)/.32);
        }

        private static double HeadWeight(double x,double y)
        {
            if(y>=190)return 0;
            double vertical=1-Smooth01((y-142)/48.0);
            double left=45+66*Smooth01((y-70)/120.0);
            double upper=Smooth01((y-15)/98.0),right=285-56*upper;
            if(y>113)right=229-25*Smooth01((y-113)/77.0);
            return vertical*Smooth01((x-left)/60.0)*(1-Smooth01((x-(right-60))/60.0));
        }

        private static double Smooth01(double value)
        {value=FiniteClamp(value,0,1);return value*value*(3-2*value);}

        private void EnsureLayers(int width,int height)
        {
            if (neutralRaster == null) neutralRaster=ReadRaster(neutralSource);
            if (cleanRaster == null) cleanRaster=ReadRaster(cleanPlate);
            if (maskWidth != width || maskHeight != height || leftMask == null)
            {
                maskWidth=width; maskHeight=height;
                leftMask=new byte[width*height]; rightMask=new byte[width*height];
                for (int y=0;y<height;y++) for(int x=0;x<width;x++)
                {
                    double px=(x+0.5)*DesignSize/width, py=(y+0.5)*DesignSize/height;
                    int i=y*width+x;
                    if (Inside(px,py,LeftEarOutline)) leftMask[i]=RootFeather(px,py,new Point(102,91),new Point(154,72));
                    if (Inside(px,py,RightEarOutline)) rightMask[i]=RootFeather(px,py,new Point(173,78),new Point(219,108));
                }
                neutralPixels=null; cleanPixels=null; animatedBitmap=null;
            }
            if (neutralPixels == null) neutralPixels=Resample(neutralRaster,width,height);
            if (cleanPixels == null)
            {
                cleanPixels=Resample(cleanRaster,width,height);
                // A transparent/corrupt auxiliary plate must not punch holes
                // into the pet. These anchor pixels are hidden forehead/body
                // under the ear roots in the approved view.
                cleanContentValid=AnchorAlpha(cleanPixels,width,height,124,84)>=64 &&
                    AnchorAlpha(cleanPixels,width,height,198,94)>=64;
            }
        }

        private static byte AnchorAlpha(byte[] pixels,int width,int height,double x,double y)
        {
            int px=Math.Max(0,Math.Min(width-1,(int)(x*width/DesignSize)));
            int py=Math.Max(0,Math.Min(height-1,(int)(y*height/DesignSize)));
            return pixels[(py*width+px)*4+3];
        }

        private void DrawEar(int width,int height,byte[] mask,Point[] outline,Point root,double degrees)
        {
            double angle=degrees*Math.PI/180, cos=Math.Cos(angle),sin=Math.Sin(angle);
            double minX=Double.MaxValue,minY=Double.MaxValue,maxX=Double.MinValue,maxY=Double.MinValue;
            foreach (Point point in outline)
            {
                double dx=point.X-root.X,dy=point.Y-root.Y;
                double x=root.X+cos*dx-sin*dy,y=root.Y+sin*dx+cos*dy;
                minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);
            }
            int x0=Math.Max(0,(int)Math.Floor((minX-2)*width/DesignSize));
            int x1=Math.Min(width-1,(int)Math.Ceiling((maxX+2)*width/DesignSize));
            int y0=Math.Max(0,(int)Math.Floor((minY-2)*height/DesignSize));
            int y1=Math.Min(height-1,(int)Math.Ceiling((maxY+2)*height/DesignSize));
            for(int y=y0;y<=y1;y++) for(int x=x0;x<=x1;x++)
            {
                double dx=(x+0.5)*DesignSize/width-root.X,dy=(y+0.5)*DesignSize/height-root.Y;
                double sx=(root.X+cos*dx+sin*dy)*width/DesignSize-0.5;
                double sy=(root.Y-sin*dx+cos*dy)*height/DesignSize-0.5;
                Sample sample=SamplePixel(neutralPixels,mask,width,height,sx,sy);
                Over(outputPixels,(y*width+x)*4,sample);
            }
        }

        private static byte RootFeather(double x,double y,Point a,Point b)
        {
            double vx=b.X-a.X,vy=b.Y-a.Y;
            double distance=-(vx*(y-a.Y)-vy*(x-a.X))/Math.Sqrt(vx*vx+vy*vy);
            return (byte)Math.Round(FiniteClamp(distance/2.4,0,1)*255);
        }

        private static bool Inside(double x,double y,Point[] points)
        {
            bool result=false;
            for(int i=0,j=points.Length-1;i<points.Length;j=i++)
                if ((points[i].Y>y)!=(points[j].Y>y) && x<(points[j].X-points[i].X)*(y-points[i].Y)/(points[j].Y-points[i].Y)+points[i].X)
                    result=!result;
            return result;
        }

        private static bool MatchingAspect(Raster body,BitmapSource other)
        {
            if (other == null || other.PixelWidth < 1 || other.PixelHeight < 1) return false;
            return Math.Abs((double)body.Width/body.Height-(double)other.PixelWidth/other.PixelHeight)<0.01;
        }

        private static Raster ReadRaster(BitmapSource bitmap)
        {
            BitmapSource converted=bitmap;
            if (bitmap.Format != PixelFormats.Pbgra32)
            {
                FormatConvertedBitmap format=new FormatConvertedBitmap(bitmap,PixelFormats.Pbgra32,null,0);
                if (format.CanFreeze) format.Freeze();
                converted=format;
            }
            Raster raster=new Raster();
            raster.Width=converted.PixelWidth;raster.Height=converted.PixelHeight;
            raster.Pixels=new byte[raster.Width*raster.Height*4];
            converted.CopyPixels(raster.Pixels,raster.Width*4,0);
            for(int i=0;i<raster.Pixels.Length;i+=4)
                if (raster.Pixels[i+3]<AlphaCutoff)
                { raster.Pixels[i]=0;raster.Pixels[i+1]=0;raster.Pixels[i+2]=0;raster.Pixels[i+3]=0; }
            raster.Bitmap=BitmapSource.Create(raster.Width,raster.Height,96,96,PixelFormats.Pbgra32,null,raster.Pixels,raster.Width*4);
            raster.Bitmap.Freeze();
            return raster;
        }

        private static byte[] Resample(Raster input,int width,int height)
        {
            if (input.Width==width && input.Height==height) return input.Pixels;
            byte[] result=new byte[width*height*4];
            for(int y=0;y<height;y++) for(int x=0;x<width;x++)
            {
                Sample sample=SamplePixel(input.Pixels,null,input.Width,input.Height,(x+0.5)*input.Width/width-0.5,(y+0.5)*input.Height/height-0.5);
                int i=(y*width+x)*4;
                result[i]=Byte(sample.B);result[i+1]=Byte(sample.G);result[i+2]=Byte(sample.R);result[i+3]=Byte(sample.A);
                if (result[i+3]<AlphaCutoff) result[i]=result[i+1]=result[i+2]=result[i+3]=0;
            }
            return result;
        }

        private static Sample SamplePixel(byte[] pixels,byte[] mask,int width,int height,double x,double y)
        {
            int x0=(int)Math.Floor(x),y0=(int)Math.Floor(y);
            double fx=x-x0,fy=y-y0;
            Sample sample=new Sample();
            AddSample(ref sample,pixels,mask,width,height,x0,y0,(1-fx)*(1-fy));
            AddSample(ref sample,pixels,mask,width,height,x0+1,y0,fx*(1-fy));
            AddSample(ref sample,pixels,mask,width,height,x0,y0+1,(1-fx)*fy);
            AddSample(ref sample,pixels,mask,width,height,x0+1,y0+1,fx*fy);
            return sample;
        }

        private static void AddSample(ref Sample sample,byte[] pixels,byte[] mask,int width,int height,int x,int y,double weight)
        {
            if (x<0 || y<0 || x>=width || y>=height || weight<=0) return;
            int pixel=y*width+x,index=pixel*4;
            if (mask!=null) weight*=mask[pixel]/255.0;
            sample.B+=pixels[index]*weight;sample.G+=pixels[index+1]*weight;sample.R+=pixels[index+2]*weight;sample.A+=pixels[index+3]*weight;
        }

        private static void Over(byte[] pixels,int index,Sample sample)
        {
            if (sample.A<0.01) return;
            double inverse=1-sample.A/255.0;
            pixels[index]=Byte(sample.B+pixels[index]*inverse);
            pixels[index+1]=Byte(sample.G+pixels[index+1]*inverse);
            pixels[index+2]=Byte(sample.R+pixels[index+2]*inverse);
            pixels[index+3]=Byte(sample.A+pixels[index+3]*inverse);
        }

        private static byte Byte(double value) { return (byte)Math.Max(0,Math.Min(255,(int)Math.Round(value))); }
        private static double FiniteClamp(double value,double minimum,double maximum)
        { return Double.IsNaN(value) || Double.IsInfinity(value) ? 0 : Math.Max(minimum,Math.Min(maximum,value)); }
        private struct Sample { internal double B,G,R,A; }
        private sealed class Raster { internal int Width,Height;internal byte[] Pixels;internal BitmapSource Bitmap; }
    }
}
