using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BaobaoDesktop;

public static class RendererTests
{
    private static int checks;
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string basePath=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..",".."));
            string asset=Path.Combine(basePath,@"assets\frames\baobao-frame-00.png");
            string eatingPath=Path.Combine(basePath,@"assets\frames\baobao-frame-05.png");
            string clean=args.Length>0?args[0]:Path.Combine(basePath,@"assets\idle-clean-plate.png");
            BitmapSource source=Load(asset),plate=Load(clean);
            IdlePetRenderer renderer=new IdlePetRenderer();
            renderer.Width=362;renderer.Height=362;renderer.Source=source;renderer.NeutralSource=source;renderer.CleanPlate=plate;renderer.LayeredIdle=true;
            RenderOptions.SetBitmapScalingMode(renderer,BitmapScalingMode.HighQuality);
            renderer.Pose=new IdleMotionPose();
            BitmapSource baseline=Capture(renderer);
            Image image=new Image();image.Width=362;image.Height=362;image.Stretch=Stretch.Fill;image.Source=Sanitize(source);
            BitmapSource expected=Capture(image);
            Check(Difference(baseline,expected,new Int32Rect(0,0,362,362))==0,"zero pose equals sanitized original pixels");
            renderer.Pose=new IdleMotionPose {LeftEarDegrees=4,RightEarDegrees=-3};
            BitmapSource ears=Capture(renderer);
            Check(Difference(ears,baseline,new Int32Rect(96,15,144,100))>0,"ear pose visibly articulates local ear pixels");
            Check(Difference(ears,baseline,new Int32Rect(109,112,105,66))==0,"ear motion keeps face pixels unchanged");
            Check(Difference(ears,baseline,new Int32Rect(0,272,134,65))==0,"ear motion keeps both bowls unchanged");
            renderer.Pose=new IdleMotionPose {TailLift=1,TailSway=1};
            BitmapSource tail=Capture(renderer);
            Check(Difference(tail,baseline,new Int32Rect(196,245,166,87))>0,"tail pose visibly bends local tail pixels");
            Check(Difference(tail,baseline,new Int32Rect(336,251,25,47))==0,"tail root pixels remain fixed");
            Check(Difference(tail,baseline,new Int32Rect(109,112,105,66))==0,"tail motion keeps face pixels unchanged");
            Check(Difference(tail,baseline,new Int32Rect(0,272,134,65))==0,"tail motion keeps bowls unchanged");
            Check(Difference(tail,baseline,new Int32Rect(129,265,67,67))==0,"tail motion keeps front paws unchanged");
            Check(Difference(tail,baseline,new Int32Rect(224,160,100,85))==0,"tail motion keeps midbody coat pixels unchanged");
            renderer.Pose=new IdleMotionPose {LeftEarDegrees=4,RightEarDegrees=-3,TailLift=1,TailSway=1};
            BitmapSource both=Capture(renderer);
            Check(NoTinyAlpha(both),"composited alpha below 12 is zero");
            renderer.CleanPlate=null;
            Check(Difference(Capture(renderer),tail,new Int32Rect(0,0,362,362))==0,"missing clean plate skips ears but preserves original-pixel tail motion");
            renderer.CleanPlate=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[8],8);
            Check(Difference(Capture(renderer),tail,new Int32Rect(0,0,362,362))==0,"mismatched clean plate skips ears but preserves original-pixel tail motion");
            renderer.CleanPlate=BitmapSource.Create(362,362,96,96,PixelFormats.Bgra32,null,new byte[362*362*4],362*4);
            Check(Difference(Capture(renderer),tail,new Int32Rect(0,0,362,362))==0,"transparent empty clean plate skips ears but preserves original-pixel tail motion");
            renderer.CleanPlate=plate;renderer.LayeredIdle=false;
            Check(Difference(Capture(renderer),baseline,new Int32Rect(0,0,362,362))==0,"disabled layered mode displays source unchanged");
            renderer.Source=Load(eatingPath);
            image.Source=Sanitize(renderer.Source);
            Check(Difference(Capture(renderer),Capture(image),new Int32Rect(0,0,362,362))==0,"eating frame is shown complete without part overlays");
            Check(VisualTreeHelper.HitTest(renderer,new Point(0,0))==null,"transparent corner does not capture hit testing");
            Check(VisualTreeHelper.HitTest(renderer,new Point(275,200))!=null,"opaque body remains hit-testable");
            string outDir=Path.Combine(basePath,@"tests\render-output");Directory.CreateDirectory(outDir);
            SaveSheet(Path.Combine(outDir,"renderer-contact-sheet.png"),baseline,ears,tail,both);
            TestPetTailSilhouette(source,plate,baseline,outDir);
            TestPetDetails(source,plate,baseline,outDir);
            TestSmoothPet(basePath,source,plate,baseline,outDir);
            TestPetGeometry();
            TestPetDetailGeometry();
            TestTailGeometry();
            Console.WriteLine("PASS "+checks+" renderer checks; source="+source.PixelWidth+"x"+source.PixelHeight+" plate="+plate.PixelWidth+"x"+plate.PixelHeight);
            return 0;
        }
        catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }

    private static void TestPetTailSilhouette(BitmapSource neutral,BitmapSource plate,BitmapSource baseline,string outDir)
    {
        IdlePetRenderer pet=new IdlePetRenderer {Width=362,Height=362,Source=neutral,NeutralSource=neutral,CleanPlate=plate,Petting=true,PetBlend=1,Pose=new IdleMotionPose {TailLift=.65,TailSway=-.3}};
        BitmapSource tip=Capture(pet);byte[] original=Pixels(baseline),pixels=Pixels(tip);
        double outward=BottomContour(pixels,211,246)-BottomContour(original,211,246);
        // The approved front paws end at x195. Pixels x199 onward around y306
        // belong to the original tail; the old plate invented toes there.
        int footChanges=Difference(pixels,original,new Int32Rect(129,265,67,67));
        SaveSheet(Path.Combine(outDir,"pet-tail-silhouette-comparison.png"),baseline,tip,baseline,tip);
        Console.WriteLine("PET_TAIL_VISIBLE outwardBottomPixels="+outward.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+" fixedFootChanged="+footChanges);
        Check(outward>=3.5&&outward<=9,"Pet terminal tail sweeps visibly into floor background rather than into abdomen");
        Check(footChanges==0,"Pet tail compositing never overwrites foreground foot pixels");
        pet.CleanPlate=null;Check(Difference(Capture(pet),tip,new Int32Rect(0,240,362,122))==0,"Pet tail never exposes generated clean-plate body or foot patches");
        pet.Petting=false;pet.PetBlend=0;pet.LayeredIdle=true;
        Check(Difference(Capture(pet),tip,new Int32Rect(0,240,362,122))==0,"idle uses the identical plate-independent tail deformation");
        double previous=BottomContour(original,211,246),largestStep=0;
        for(int time=0;time<=9600;time+=40)
        {
            double a=time*Math.PI*2/4800;pet.Pose=new IdleMotionPose {TailLift=.65*(1-Math.Cos(a))/2,TailSway=.55*Math.Sin(a)};
            byte[] frame=Pixels(Capture(pet));double contour=BottomContour(frame,211,246);largestStep=Math.Max(largestStep,Math.Abs(contour-previous));previous=contour;
            Check(Difference(frame,original,new Int32Rect(129,265,67,67))==0,"tail trajectory preserves actual foreground paws");
            Check(Difference(frame,original,new Int32Rect(303,240,59,110))==0,"tail trajectory preserves fixed outer curve and root");
        }
        Console.WriteLine("TAIL_CONTOUR max40msBottomStep="+largestStep.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px");
        Check(largestStep<=1,"outward tail silhouette moves continuously across two complete cycles");
    }
    private static double BottomContour(byte[] pixels,int left,int right)
    {
        double sum=0;for(int x=left;x<=right;x++){int bottom=285;for(int y=285;y<350;y++)if(pixels[(y*362+x)*4+3]>=128)bottom=y;sum+=bottom;}return sum/(right-left+1);
    }

    private static void TestPetDetails(BitmapSource neutral,BitmapSource plate,BitmapSource baseline,string outDir)
    {
        IdlePetRenderer pet=new IdlePetRenderer {Width=362,Height=362,Source=neutral,NeutralSource=neutral,CleanPlate=plate,Petting=true,PetBlend=1,PetSway=.5,LayeredIdle=false};
        BitmapSource headOnly=Capture(pet);
        pet.Pose=new IdleMotionPose {TailLift=.65,TailSway=.5};BitmapSource tail=Capture(pet);
        int tailChanges=Difference(headOnly,tail,new Int32Rect(196,245,166,87));
        pet.Pose=new IdleMotionPose {LeftEarDegrees=3.5};BitmapSource ear=Capture(pet);
        int earChanges=Difference(headOnly,ear,new Int32Rect(75,10,174,112));
        System.Reflection.PropertyInfo breath=typeof(IdlePetRenderer).GetProperty("PetBreath");
        Console.WriteLine("PET_DETAILS_EVIDENCE tailChanged="+tailChanges+" earChanged="+earChanges+" breathAPI="+(breath!=null));
        Check(tailChanges>100&&earChanges>20&&breath!=null,"Pet honors local ear/tail Pose and exposes continuous PetBreath");
        pet.Pose=new IdleMotionPose();pet.PetBlend=0;pet.PetSway=0;
        SetPet(pet,"PetBreath",0.0);Check(Difference(Capture(pet),baseline,new Int32Rect(0,0,362,362))==0,"Pet details all zero equal exact idle0");
        SetPet(pet,"PetBreath",1.0);BitmapSource inhale=Capture(pet);
        Check(Difference(inhale,baseline,new Int32Rect(110,158,94,75))>100,"Pet breath visibly changes upper-chest texture locally");
        Check(Difference(inhale,baseline,new Int32Rect(0,0,362,155))==0,"Pet breath alone leaves head and face identical");
        Check(Difference(inhale,baseline,new Int32Rect(0,235,362,127))==0,"Pet breath alone leaves abdomen paws bowls tail identical");
        SetPet(pet,"PetBreath",Double.NaN);Check(Difference(Capture(pet),baseline,new Int32Rect(0,0,362,362))==0,"nonfinite Pet breath returns to neutral");
        SaveSheet(Path.Combine(outDir,"pet-details-contact-sheet.png"),headOnly,ear,tail,inhale);
        byte[] original=Pixels(baseline);Point previous=new Point();bool hasPrevious=false;
        double maximumStep=0,maximumTailStep=0;Point previousTail=new Point();
        Int32Rect[] locked=new Int32Rect[] {new Int32Rect(229,113,126,130),new Int32Rect(80,235,120,38),new Int32Rect(0,270,135,65),new Int32Rect(126,272,69,61),new Int32Rect(336,251,25,47)};
        for(int elapsed=0;elapsed<=7400;elapsed+=40)
        {
            double blend=elapsed<320?Smooth(elapsed/320.0):elapsed<=6400?1:1-Smooth((elapsed-6400)/500.0);
            pet.PetBlend=blend;pet.PetSway=Math.Sin(elapsed*Math.PI*2/3200.0)*blend;
            pet.Pose=new IdleMotionPose {LeftEarDegrees=3*Math.Pow(Math.Max(0,Math.Sin((elapsed-950)*Math.PI/1400)),4)*blend,RightEarDegrees=-2.5*Math.Pow(Math.Max(0,Math.Sin((elapsed-4650)*Math.PI/1400)),4)*blend,TailLift=.65*(1-Math.Cos(elapsed*Math.PI*2/4800.0))/2*blend,TailSway=.6*Math.Sin(elapsed*Math.PI*2/4800.0)*blend};
            SetPet(pet,"PetBreath",(1-Math.Cos(elapsed*Math.PI*2/4000.0))/2*blend);
            BitmapSource sample=Capture(pet);byte[] pixels=Pixels(sample);Point center=HeadCentroid(pixels),tailCenter=RegionCentroid(pixels,new Int32Rect(196,292,140,44));
            if(hasPrevious){maximumStep=Math.Max(maximumStep,(center-previous).Length);maximumTailStep=Math.Max(maximumTailStep,(tailCenter-previousTail).Length);}
            previous=center;previousTail=tailCenter;hasPrevious=true;
            foreach(Int32Rect area in locked)Check(Difference(pixels,original,area)==0,"combined Pet details preserve back belly paws bowls and fixed tail root");
            Check(MinimumAlpha(pixels,new Int32Rect(125,95,69,50))>=220,"combined Pet details have no opaque-face holes");
            Check(NoTinyAlpha(sample),"combined Pet premultiplied sampling has no tiny-alpha fringe");
        }
        Console.WriteLine("PET_DETAIL_CONTINUITY maxHead40ms="+maximumStep.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px maxTail40ms="+maximumTailStep.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px");
        Check(maximumStep<=1.2&&maximumTailStep<=.8,"combined Pet ear head and tail movement stays continuous at 40ms");
        Check(Difference(Capture(pet),baseline,new Int32Rect(0,0,362,362))==0,"combined Pet return removes all details to exact idle0");
        pet.PetBlend=1;pet.PetSway=.5;pet.Pose=new IdleMotionPose {LeftEarDegrees=2,TailLift=.5,TailSway=.3};SetPet(pet,"PetBreath",.6);
        BitmapSource paused=Capture(pet);for(int i=0;i<6;i++)Check(Difference(Capture(pet),paused,new Int32Rect(0,0,362,362))==0,"held detail inputs freeze without an internal renderer clock");
    }

    private static void TestSmoothPet(string project,BitmapSource neutral,BitmapSource plate,BitmapSource baseline,string outDir)
    {
        string frames=Path.Combine(project,@"assets\frames");
        BitmapSource[] old=new BitmapSource[4];for(int i=0;i<4;i++)old[i]=Load(Path.Combine(frames,"baobao-frame-"+(20+i)+".png"));
        Image legacy=new Image {Width=362,Height=362,Stretch=Stretch.Fill,Source=Sanitize(old[1])};
        BitmapSource old21=Capture(legacy);legacy.Source=Sanitize(old[2]);BitmapSource old22=Capture(legacy);
        Point oldA=HeadCentroid(Pixels(old21)),oldB=HeadCentroid(Pixels(old22));
        double oldJump=(oldB-oldA).Length;
        Console.WriteLine("EVIDENCE old Pet21->22 alpha-centroid jump="+oldJump.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px (362 canvas)");
        Check(oldJump>1.2,"legacy sparse Pet frames reproduce a head-position jump above continuity limit");
        IdlePetRenderer pet=new IdlePetRenderer {Width=362,Height=362,Source=old[0],NeutralSource=neutral,CleanPlate=plate,LayeredIdle=false};
        SetPet(pet,"PetClosedSource",Load(Path.Combine(project,@"assets\frames\baobao-frame-02.png")));
        SetPet(pet,"Petting",true);SetPet(pet,"PetBlend",0.0);SetPet(pet,"PetSway",0.0);
        Check(Difference(Capture(pet),baseline,new Int32Rect(0,0,362,362))==0,"Pet entry at zero blend is exact idle0 even when Source is frame20");
        byte[] original=Pixels(baseline);Point previous=new Point();bool hasPrevious=false;
        double maximumStep=0,maximumTravel=0;Point origin=HeadCentroid(original);
        BitmapSource peak=null,otherPeak=null;
        Int32Rect[] locked=new Int32Rect[] {new Int32Rect(229,113,126,160),new Int32Rect(80,195,150,80),new Int32Rect(0,270,135,65),new Int32Rect(126,272,110,61),new Int32Rect(201,250,160,83)};
        for(int elapsed=0;elapsed<=7400;elapsed+=40)
        {
            double blend=elapsed<320?Smooth(elapsed/320.0):elapsed<=6400?1:1-Smooth((elapsed-6400)/500.0);
            double sway=Math.Sin(elapsed*Math.PI*2/3200.0)*blend;
            pet.Source=old[(elapsed/1000)%4];
            SetPet(pet,"PetBlend",blend);SetPet(pet,"PetSway",sway);
            BitmapSource rendered=Capture(pet);byte[] pixels=Pixels(rendered);Point center=HeadCentroid(pixels);
            if(hasPrevious){double step=(center-previous).Length;maximumStep=Math.Max(maximumStep,step);Check(step<=1.2,"40ms Pet head-alpha centroid remains continuous");}
            maximumTravel=Math.Max(maximumTravel,(center-origin).Length);previous=center;hasPrevious=true;
            foreach(Int32Rect area in locked)Check(Difference(pixels,original,area)==0,"Pet keeps body/feet/bowls/tail locked under legacy Source changes");
            Check(MinimumAlpha(pixels,new Int32Rect(125,95,69,50))>=220,"Pet face interior remains opaque without warp holes");
            Check(NoTinyAlpha(rendered),"Pet bilinear output has no low-alpha residue");
            if(elapsed==800)peak=rendered;if(elapsed==2400)otherPeak=rendered;
        }
        double fullSwing=(HeadCentroid(Pixels(peak))-HeadCentroid(Pixels(otherPeak))).Length;
        SaveSheet(Path.Combine(outDir,"pet-amplitude-contact-sheet.png"),baseline,peak,baseline,otherPeak);
        Console.WriteLine("PET_AMPLITUDE measuredPeak="+maximumTravel.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px fullSwing="+fullSwing.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px max40msStep="+maximumStep.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px");
        // The reviewed, nonfolding geometry targets a visibly larger ~5..8px
        // peak. Do not chase the old jumping frame's span by distorting ears.
        Check(maximumTravel>=4.8&&maximumTravel<=9,"continuous Pet peak head displacement is visibly about 5..8 pixels");
        Check(fullSwing>=9&&fullSwing<=17,"continuous Pet full side-to-side swing is visibly larger than the weak version");
        Check(maximumTravel>.1,"continuous Pet actually moves the head rather than freezing the pose");
        SetPet(pet,"PetBlend",1.0);SetPet(pet,"PetSway",.35);pet.Source=old[1];BitmapSource frozen=Capture(pet);
        for(int i=0;i<12;i++){pet.Source=old[i%4];Check(Difference(Capture(pet),frozen,new Int32Rect(0,0,362,362))==0,"held paused Pet inputs freeze every pixel despite Source changes");}
        SetPet(pet,"PetBlend",Double.NaN);SetPet(pet,"PetSway",Double.PositiveInfinity);
        Check(Difference(Capture(pet),baseline,new Int32Rect(0,0,362,362))==0,"nonfinite Pet values clamp to exact neutral baseline");
        SetPet(pet,"PetBlend",0.0);SetPet(pet,"PetSway",0.0);
        foreach(BitmapSource source in old){pet.Source=source;Check(Difference(Capture(pet),baseline,new Int32Rect(0,0,362,362))==0,"Pet return zero envelope never flashes old Pet source");}
        pet.Source=neutral;SetPet(pet,"Petting",false);
        Check(Difference(Capture(pet),baseline,new Int32Rect(0,0,362,362))==0,"Petting off after return matches idle baseline exactly");
        SaveSheet(Path.Combine(outDir,"pet-continuity-contact-sheet.png"),old21,old22,peak,otherPeak);
        Console.WriteLine("PET_CONTINUITY oldJump="+oldJump.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px newMax40msStep="+maximumStep.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px newMaxTravel="+maximumTravel.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px");
        WarmPetBenchmark(pet);
    }
    private static void WarmPetBenchmark(IdlePetRenderer renderer)
    {
        System.Reflection.MethodInfo ensure=typeof(IdlePetRenderer).GetMethod("EnsureFrame",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        renderer.Petting=true;renderer.PetBlend=1;
        double[] times=new double[120];
        for(int i=-20;i<times.Length;i++)
        {
            renderer.PetSway=Math.Sin((i+21)*.173);
            renderer.Pose=new IdleMotionPose {LeftEarDegrees=2*Math.Sin(i*.13),TailLift=.55*(1+Math.Sin(i*.08))/2,TailSway=.4*Math.Sin(i*.08)};
            renderer.PetBreath=(1+Math.Sin(i*.06))/2;
            System.Diagnostics.Stopwatch watch=System.Diagnostics.Stopwatch.StartNew();ensure.Invoke(renderer,null);watch.Stop();
            if(i>=0)times[i]=watch.Elapsed.TotalMilliseconds;
        }
        double sum=0;foreach(double value in times)sum+=value;Array.Sort(times);
        Console.WriteLine("PET_RENDER_WARM samples=120 meanMs="+(sum/times.Length).ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+" p95Ms="+times[(int)(times.Length*.95)-1].ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+" maxMs="+times[times.Length-1].ToString("0.000",System.Globalization.CultureInfo.InvariantCulture));
    }
    private static void TestPetGeometry()
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
        Func<double,double,double> weight=(Func<double,double,double>)Delegate.CreateDelegate(typeof(Func<double,double,double>),typeof(IdlePetRenderer).GetMethod("HeadWeight",flags));
        Func<double,double,double,double,double,double,Point> inverse=(Func<double,double,double,double,double,double,Point>)Delegate.CreateDelegate(typeof(Func<double,double,double,double,double,double,Point>),typeof(IdlePetRenderer).GetMethod("InversePetPoint",flags));
        double degrees=(double)typeof(IdlePetRenderer).GetField("PetHeadDegrees",flags).GetRawConstantValue(),shiftX=(double)typeof(IdlePetRenderer).GetField("PetHeadShiftX",flags).GetRawConstantValue(),shiftY=(double)typeof(IdlePetRenderer).GetField("PetHeadShiftY",flags).GetRawConstantValue();
        double minDet=Double.MaxValue,minSingular=Double.MaxValue,maxResidual=0;
        for(int n=-4;n<=4;n++)
        {
            double sway=n/4.0,cos=Math.Cos(sway*degrees*Math.PI/180),sin=Math.Sin(sway*degrees*Math.PI/180),tx=sway*shiftX,ty=sway*shiftY;
            for(double y=0;y<=192;y+=3)for(double x=35;x<=310;x+=3)
            {
                double a=weight(x,y),wx=(weight(x+.02,y)-weight(x-.02,y))/.04,wy=(weight(x,y+.02)-weight(x,y-.02))/.04;
                double dx=(cos-1)*(x-158)-sin*(y-153)+tx,dy=sin*(x-158)+(cos-1)*(y-153)+ty;
                double j00=1+a*(cos-1)+dx*wx,j01=-a*sin+dx*wy,j10=a*sin+dy*wx,j11=1+a*(cos-1)+dy*wy;
                double det=j00*j11-j01*j10,q=j00*j00+j01*j01+j10*j10+j11*j11;
                double singular=Math.Sqrt(Math.Max(0,(q-Math.Sqrt(Math.Max(0,q*q-4*det*det)))/2));
                minDet=Math.Min(minDet,det);minSingular=Math.Min(minSingular,singular);
                Point origin=inverse(x,y,cos,sin,tx,ty);double w=weight(origin.X,origin.Y),rx=origin.X-158,ry=origin.Y-153;
                double ex=origin.X+w*((cos-1)*rx-sin*ry+tx)-x,ey=origin.Y+w*(sin*rx+(cos-1)*ry+ty)-y;
                maxResidual=Math.Max(maxResidual,Math.Sqrt(ex*ex+ey*ey));
            }
        }
        Console.WriteLine("PET_GEOMETRY minDet="+minDet.ToString("0.000000",System.Globalization.CultureInfo.InvariantCulture)+" minSingular="+minSingular.ToString("0.000000",System.Globalization.CultureInfo.InvariantCulture)+" maxInverseResidual="+maxResidual.ToString("0.000000",System.Globalization.CultureInfo.InvariantCulture)+"px");
        Check(minDet>.12&&minSingular>.1,"strong Pet warp has positive well-conditioned Jacobian without foldover");
        Check(maxResidual<.08,"strong Pet inverse samples project back within .08 pixels");
    }
    private static void TestPetDetailGeometry()
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
        Func<double,double,double> head=(Func<double,double,double>)Delegate.CreateDelegate(typeof(Func<double,double,double>),typeof(IdlePetRenderer).GetMethod("HeadWeight",flags));
        Func<double,double,double> chest=(Func<double,double,double>)Delegate.CreateDelegate(typeof(Func<double,double,double>),typeof(IdlePetRenderer).GetMethod("BreathWeight",flags));
        Func<double,double,double,double,double,double,double,Point> inverse=(Func<double,double,double,double,double,double,double,Point>)Delegate.CreateDelegate(typeof(Func<double,double,double,double,double,double,double,Point>),typeof(IdlePetRenderer).GetMethod("InversePetDetailPoint",flags));
        double degrees=(double)typeof(IdlePetRenderer).GetField("PetHeadDegrees",flags).GetRawConstantValue(),shiftX=(double)typeof(IdlePetRenderer).GetField("PetHeadShiftX",flags).GetRawConstantValue(),shiftY=(double)typeof(IdlePetRenderer).GetField("PetHeadShiftY",flags).GetRawConstantValue();
        double minDet=Double.MaxValue,maxResidual=0,maxChestShift=0;
        for(int n=-4;n<=4;n++)for(int b=0;b<=2;b++)
        {
            double sway=n/4.0,breath=b/2.0,cos=Math.Cos(sway*degrees*Math.PI/180),sin=Math.Sin(sway*degrees*Math.PI/180),tx=sway*shiftX,ty=sway*shiftY;
            Func<double,double,Point> forward=delegate(double x,double y){double w=head(x,y),rx=x-158,ry=y-153;return new Point(x+w*((cos-1)*rx-sin*ry+tx),y+w*(sin*rx+(cos-1)*ry+ty)-breath*.8*chest(x,y));};
            for(double y=0;y<=234;y+=3)for(double x=35;x<=310;x+=3)
            {
                Point xp=forward(x+.02,y),xm=forward(x-.02,y),yp=forward(x,y+.02),ym=forward(x,y-.02);
                double det=((xp.X-xm.X)*(yp.Y-ym.Y)-(yp.X-ym.X)*(xp.Y-xm.Y))/.0016;minDet=Math.Min(minDet,det);
                Point origin=inverse(x,y,cos,sin,tx,ty,breath),projected=forward(origin.X,origin.Y);maxResidual=Math.Max(maxResidual,(projected-new Point(x,y)).Length);
                maxChestShift=Math.Max(maxChestShift,breath*.8*chest(x,y));
            }
        }
        Console.WriteLine("PET_DETAIL_GEOMETRY minDet="+minDet.ToString("0.000000",System.Globalization.CultureInfo.InvariantCulture)+" maxInverseResidual="+maxResidual.ToString("0.000000",System.Globalization.CultureInfo.InvariantCulture)+"px maxBreath="+maxChestShift.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"px");
        Check(minDet>.12&&maxResidual<.08,"combined chest/head displacement remains nonfolding and reverse-mapping stable");
        Check(maxChestShift>.7&&maxChestShift<=.8,"breathing displacement is subpixel and limited to upper chest");
    }
    private static void TestTailGeometry()
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
        Func<double,double,double> weight=(Func<double,double,double>)Delegate.CreateDelegate(typeof(Func<double,double,double>),typeof(IdlePetRenderer).GetMethod("PetTailWeight",flags));
        Func<double,double,double,double,Point> inverse=(Func<double,double,double,double,Point>)Delegate.CreateDelegate(typeof(Func<double,double,double,double,Point>),typeof(IdlePetRenderer).GetMethod("InversePetTailPoint",flags));
        double minDet=Double.MaxValue,maxResidual=0;
        for(int l=0;l<=4;l++)for(int s=-4;s<=4;s++)
        {
            double lift=l/4.0,sway=s/4.0,tx=-1.5*lift+1.2*sway,ty=10*lift;
            for(double y=280;y<=366;y+=2)for(double x=194;x<=306;x+=2)
            {
                double wx=(weight(x+.02,y)-weight(x-.02,y))/.04,wy=(weight(x,y+.02)-weight(x,y-.02))/.04;
                minDet=Math.Min(minDet,1+tx*wx+ty*wy);
                Point origin=inverse(x,y,tx,ty);double w=weight(origin.X,origin.Y);
                maxResidual=Math.Max(maxResidual,(new Point(origin.X+tx*w,origin.Y+ty*w)-new Point(x,y)).Length);
            }
        }
        Console.WriteLine("TAIL_GEOMETRY minDet="+minDet.ToString("0.000000",System.Globalization.CultureInfo.InvariantCulture)+" maxInverseResidual="+maxResidual.ToString("0.000000",System.Globalization.CultureInfo.InvariantCulture)+"px");
        Check(minDet>.25&&maxResidual<.05,"common tail field remains nonfolding and inverse-stable across full input limits");
    }
    private static void SetPet(IdlePetRenderer renderer,string property,object value)
    {System.Reflection.PropertyInfo member=typeof(IdlePetRenderer).GetProperty(property);Check(member!=null,"renderer exposes continuous Pet API "+property);member.SetValue(renderer,value,null);}
    private static double Smooth(double n){n=Math.Max(0,Math.Min(1,n));return n*n*(3-2*n);}
    private static byte[] Pixels(BitmapSource source){byte[] pixels=new byte[362*362*4];source.CopyPixels(pixels,362*4,0);return pixels;}
    private static Point HeadCentroid(byte[] pixels)
    {
        double mass=0,xsum=0,ysum=0;for(int y=8;y<177;y++)for(int x=81;x<241;x++)
        {double a=pixels[(y*362+x)*4+3];if(a<12)continue;mass+=a;xsum+=x*a;ysum+=y*a;}
        if(mass==0)throw new Exception("empty head centroid region");return new Point(xsum/mass,ysum/mass);
    }
    private static Point RegionCentroid(byte[] pixels,Int32Rect area)
    {
        double mass=0,xsum=0,ysum=0;for(int y=area.Y;y<area.Y+area.Height;y++)for(int x=area.X;x<area.X+area.Width;x++)
        {double a=pixels[(y*362+x)*4+3];if(a<12)continue;mass+=a;xsum+=x*a;ysum+=y*a;}
        return new Point(xsum/mass,ysum/mass);
    }
    private static int MinimumAlpha(byte[] pixels,Int32Rect area)
    {int minimum=255;for(int y=area.Y;y<area.Y+area.Height;y++)for(int x=area.X;x<area.X+area.Width;x++)minimum=Math.Min(minimum,pixels[(y*362+x)*4+3]);return minimum;}
    private static int Difference(byte[] a,byte[] b,Int32Rect area)
    {int count=0;for(int y=area.Y;y<area.Y+area.Height;y++)for(int x=area.X;x<area.X+area.Width;x++){int i=(y*362+x)*4;for(int c=0;c<4;c++)if(a[i+c]!=b[i+c]){count++;break;}}return count;}

    private static BitmapSource Load(string path)
    {
        BitmapImage image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();return image;
    }
    private static BitmapSource Sanitize(BitmapSource input)
    {
        BitmapSource converted=new FormatConvertedBitmap(input,PixelFormats.Pbgra32,null,0);
        byte[] pixels=new byte[converted.PixelWidth*converted.PixelHeight*4];converted.CopyPixels(pixels,converted.PixelWidth*4,0);
        for(int i=0;i<pixels.Length;i+=4)if(pixels[i+3]<12)pixels[i]=pixels[i+1]=pixels[i+2]=pixels[i+3]=0;
        return BitmapSource.Create(converted.PixelWidth,converted.PixelHeight,96,96,PixelFormats.Pbgra32,null,pixels,converted.PixelWidth*4);
    }
    private static BitmapSource Capture(FrameworkElement element)
    {
        element.Measure(new Size(362,362));element.Arrange(new Rect(0,0,362,362));element.UpdateLayout();
        RenderTargetBitmap target=new RenderTargetBitmap(362,362,96,96,PixelFormats.Pbgra32);target.Render(element);return target;
    }
    private static int Difference(BitmapSource a,BitmapSource b,Int32Rect area)
    {
        byte[] aa=new byte[362*362*4],bb=new byte[aa.Length];a.CopyPixels(aa,362*4,0);b.CopyPixels(bb,362*4,0);
        int count=0;for(int y=area.Y;y<area.Y+area.Height;y++)for(int x=area.X;x<area.X+area.Width;x++)
        {int i=(y*362+x)*4;for(int c=0;c<4;c++)if(aa[i+c]!=bb[i+c]){count++;break;}}return count;
    }
    private static bool NoTinyAlpha(BitmapSource image)
    {
        byte[] pixels=new byte[362*362*4];image.CopyPixels(pixels,362*4,0);
        for(int i=3;i<pixels.Length;i+=4)if(pixels[i]>0&&pixels[i]<12)return false;return true;
    }
    private static void SaveSheet(string path,params BitmapSource[] images)
    {
        DrawingVisual visual=new DrawingVisual();using(DrawingContext dc=visual.RenderOpen())
        {dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(238,237,233)),null,new Rect(0,0,724,724));for(int i=0;i<images.Length;i++)dc.DrawImage(images[i],new Rect(i%2*362,i/2*362,362,362));}
        RenderTargetBitmap sheet=new RenderTargetBitmap(724,724,96,96,PixelFormats.Pbgra32);sheet.Render(visual);
        PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(sheet));using(FileStream stream=File.Create(path))encoder.Save(stream);
    }
    private static void Check(bool value,string message){if(!value)throw new Exception("FAIL "+message);checks++;Console.WriteLine("PASS "+message);}
}
