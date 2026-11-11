using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BaobaoDesktop
{
    public static class DropRouterTests
    {
        private static int checks;
        private static readonly Dictionary<string,byte[]> snapshots=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,FileAttributes> attributes=new Dictionary<string,FileAttributes>(StringComparer.OrdinalIgnoreCase);
        private const string GoodScratcher="{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\",\"displayName\":\"包包的猫抓板\"}";
        private const string GoodTree="{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"cat_tree\",\"displayName\":\"柱形猫爬架\"}";

        public static int Main()
        {
            try
            {
                string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"..","drop-router-test","run-"+Guid.NewGuid().ToString("N")));
                string appFixtures=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"fixtures-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);Directory.CreateDirectory(appFixtures);
                DropRouter router=new DropRouter();
                string ordinary=Fixture(root,"普通文件.txt","This is a self-created ordinary fixture. It must never be changed by routing.");
                string contentOnly=Fixture(root,"plain.json",GoodScratcher);
                string toy=Fixture(root,"包包抓板.baobao-toy.json",GoodScratcher);
                string tree=Fixture(root,"柱形架.baobao-toy.json",GoodTree);
                Expect(router,new string[]{ordinary},DropKind.OrdinaryFiles,"files.recycle",null);
                Expect(router,new string[]{ordinary,contentOnly},DropKind.OrdinaryFiles,"files.recycle",null);
                Expect(router,new string[]{contentOnly},DropKind.OrdinaryFiles,"files.recycle",null);
                DropPlan toyPlan=Expect(router,new string[]{toy},DropKind.Toy,"toy.scratcher","已保留");
                Check(toyPlan.DisplayName=="包包的猫抓板","声明的中文displayName被保留");
                Check(toyPlan.Message.IndexOf("后续",StringComparison.Ordinal)<0 && toyPlan.Message.IndexOf("未实现",StringComparison.Ordinal)<0,"V1.0有效玩具不显示未实现");
                Expect(router,new string[]{tree},DropKind.Toy,"toy.cat_tree",null);
                Expect(router,new string[]{Fixture(root,"UPPER.BAOBAO-TOY.JSON",GoodTree)},DropKind.Toy,"toy.cat_tree",null);
                Expect(router,new string[]{Fixture(root,"default-name.baobao-toy.json","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\"}")},DropKind.Toy,"toy.scratcher",null);
                Expect(router,new string[]{Fixture(appFixtures,"应用目录玩具.baobao-toy.json",GoodTree)},DropKind.Toy,"toy.cat_tree",null);

                string readonlyToy=Fixture(root,"readonly.baobao-toy.json",GoodScratcher);
                File.SetAttributes(readonlyToy,FileAttributes.ReadOnly);attributes[readonlyToy]=File.GetAttributes(readonlyToy);
                Expect(router,new string[]{readonlyToy},DropKind.Toy,"toy.scratcher",null);
                string bom=Path.Combine(root,"utf8-bom.baobao-toy.json");
                byte[] payload=Encoding.UTF8.GetBytes(GoodScratcher),withBom=new byte[payload.Length+3];
                withBom[0]=239;withBom[1]=187;withBom[2]=191;Buffer.BlockCopy(payload,0,withBom,3,payload.Length);
                FixtureBytes(bom,withBom);Expect(router,new string[]{bom},DropKind.Toy,"toy.scratcher",null);

                string exact=Path.Combine(root,"exact-size.baobao-toy.json");
                string padding=GoodScratcher+new string(' ',ToyManifestExtension.MaximumBytes-Encoding.UTF8.GetByteCount(GoodScratcher));
                FixtureBytes(exact,Encoding.UTF8.GetBytes(padding));
                Expect(router,new string[]{exact},DropKind.Toy,"toy.scratcher",null);
                string oversized=Fixture(root,"oversized.baobao-toy.json",GoodScratcher+new string(' ',ToyManifestExtension.MaximumBytes));
                Expect(router,new string[]{oversized},DropKind.Rejected,null,"16 KiB");

                string[,] bad=new string[,] {
                    {"empty",""},{"broken","{not-json"},{"trailing","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\"} garbage"},
                    {"two-objects",GoodScratcher+GoodTree},
                    {"two-complementary-objects","{\"schema\":\"baobao.toy\",\"version\":1}{\"kind\":\"scratcher\"}"},
                    {"leading-zero-version","{\"schema\":\"baobao.toy\",\"version\":01,\"kind\":\"scratcher\"}"},
                    {"trailing-comma","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\",}"},
                    {"schema","{\"schema\":\"other\",\"version\":1,\"kind\":\"scratcher\"}"},
                    {"version","{\"schema\":\"baobao.toy\",\"version\":2,\"kind\":\"scratcher\"}"},
                    {"string-version","{\"schema\":\"baobao.toy\",\"version\":\"1\",\"kind\":\"scratcher\"}"},
                    {"fraction-version","{\"schema\":\"baobao.toy\",\"version\":1.5,\"kind\":\"scratcher\"}"},
                    {"unknown-kind","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"unknown_kind\"}"},
                    {"missing-kind","{\"schema\":\"baobao.toy\",\"version\":1}"},
                    {"script","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\",\"script\":\"do-not-run\"}"},
                    {"url","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"cat_tree\",\"url\":\"https://example.invalid/toy\"}"},
                    {"type-metadata","{\"__type\":\"DoNotActivate\",\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\"}"},
                    {"duplicate","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\",\"kind\":\"cat_tree\"}"},
                    {"nested","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":{\"name\":\"scratcher\"}}"},
                    {"array","["+GoodScratcher+"]"},{"null","null"},
                    {"empty-name","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\",\"displayName\":\"\"}"},
                    {"control-name","{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"scratcher\",\"displayName\":\"bad\\nname\"}"}
                };
                for(int i=0;i<bad.GetLength(0);i++)
                    Expect(router,new string[]{Fixture(root,bad[i,0]+".baobao-toy.json",bad[i,1])},DropKind.Rejected,null,null);
                string invalidUtf8=Path.Combine(root,"bad-utf8.baobao-toy.json");FixtureBytes(invalidUtf8,new byte[]{255,254,255});
                Expect(router,new string[]{invalidUtf8},DropKind.Rejected,null,"UTF-8");

                Expect(router,new string[]{toy,ordinary},DropKind.Rejected,null,"分开投放");
                Expect(router,new string[]{toy,tree},DropKind.Rejected,null,"一次只接收一件");
                Expect(router,new string[]{toy,toy},DropKind.Rejected,null,"一次只接收一件");
                Expect(router,new string[]{Path.Combine(root,"unknown-kind.baobao-toy.json"),ordinary},DropKind.Rejected,null,"分开投放");
                Expect(router,new string[]{"relative.baobao-toy.json"},DropKind.Rejected,null,"本地绝对");
                Expect(router,new string[]{@"\\server\share\x.baobao-toy.json"},DropKind.Rejected,null,"本地绝对");
                Expect(router,new string[]{"https://example.invalid/x.baobao-toy.json"},DropKind.Rejected,null,"本地绝对");
                Expect(router,new string[]{toy+":stream"},DropKind.Rejected,null,"路径无效");
                Expect(router,new string[]{toy+"."},DropKind.Rejected,null,"路径无效");
                Expect(router,new string[]{toy+"\\"},DropKind.Rejected,null,"路径无效");
                Expect(router,new string[]{Path.Combine(root,"absent.baobao-toy.json")},DropKind.Rejected,null,"不存在");
                string directory=Path.Combine(root,"directory.baobao-toy.json");Directory.CreateDirectory(directory);
                Expect(router,new string[]{directory},DropKind.Rejected,null,"普通文件");
                Expect(router,new string[0],DropKind.Rejected,null,null);
                Expect(router,null,DropKind.Rejected,null,null);
                Expect(router,new string[]{""},DropKind.Rejected,null,null);

                StringBuilder shortName=new StringBuilder(32768);
                if (GetShortPathName(toy,shortName,shortName.Capacity)>0)
                {
                    Expect(router,new string[]{shortName.ToString()},DropKind.Toy,"toy.scratcher",null);
                    Console.WriteLine("SHORT_ALIAS "+shortName);
                }
                string link=Path.Combine(root,"linked.baobao-toy.json");
                if (CreateSymbolicLink(link,toy,2)||CreateSymbolicLink(link,toy,0))
                    Expect(router,new string[]{link},DropKind.Rejected,null,"重解析");
                else Console.WriteLine("SKIP symbolic link creation unavailable");

                TestPngRouting(router,root,appFixtures,ordinary);
                TestExtensionOrder(root,toy,ordinary);
                foreach(KeyValuePair<string,byte[]> snapshot in snapshots)
                {
                    Check(File.Exists(snapshot.Key),"no mutation: file remains "+Path.GetFileName(snapshot.Key));
                    Check(Equal(snapshot.Value,File.ReadAllBytes(snapshot.Key)),"no mutation: content preserved "+Path.GetFileName(snapshot.Key));
                    Check(File.GetAttributes(snapshot.Key)==attributes[snapshot.Key],"no mutation: attributes preserved "+Path.GetFileName(snapshot.Key));
                }
                Console.WriteLine("PASS "+checks+" checks; fixtures="+root+"; appFixtures="+appFixtures);
                Console.WriteLine("只读路由测试；未调用回收服务，未删除、移动或改写任何投放文件。");
                return 0;
            }
            catch(Exception e){Console.Error.WriteLine("FAIL "+e);return 1;}
        }

        private static void TestPngRouting(DropRouter router,string root,string appFixtures,string ordinary)
        {
            string project=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"..",".."));
            byte[] plain=File.ReadAllBytes(Path.Combine(project,@"assets\toy-scratcher.png"));
            byte[] packaged=File.ReadAllBytes(Path.Combine(project,@"toy-manifests\猫抓板.baobao-toy.png"));
            byte[] packagedTree=File.ReadAllBytes(Path.Combine(project,@"toy-manifests\柱形猫爬架.baobao-toy.png"));
            Check(Crc32(Encoding.ASCII.GetBytes("123456789"),0,9)==0xcbf43926,"PNG CRC helper matches known vector");
            Check(Equal(plain,WithoutChunks(packaged,delegate(PngPart p){return p.Type=="tEXt"&&TextKeyword(p.Data)=="baobao.toy";})),"PNG玩具只新增声明块，原压缩像素及全部原块逐字节不变");
            string good=FixtureRaw(root,"图片抓板.baobao-toy.png",packaged);
            string tree=FixtureRaw(root,"图片爬架.baobao-toy.png",packagedTree);
            DropPlan result=Expect(router,new string[]{good},DropKind.Toy,"toy.scratcher","已保留");
            Check(result.DisplayName=="猫抓板","PNG内ASCII JSON的Unicode转义恢复中文名称");
            Expect(router,new string[]{tree},DropKind.Toy,"toy.cat_tree",null);
            Expect(router,new string[]{FixtureRaw(appFixtures,"应用目录图片.baobao-toy.png",packaged)},DropKind.Toy,"toy.scratcher",null);
            Expect(router,new string[]{FixtureRaw(root,"普通图片.png",plain)},DropKind.OrdinaryFiles,"files.recycle",null);
            Expect(router,new string[]{FixtureRaw(root,"无清单.baobao-toy.png",plain)},DropKind.Rejected,null,"清单");

            byte[] bad=(byte[])packaged.Clone();bad[0]=0;
            Expect(router,new string[]{FixtureRaw(root,"坏签名.baobao-toy.png",bad)},DropKind.Rejected,null,"格式损坏");
            bad=new byte[packaged.Length-1];Buffer.BlockCopy(packaged,0,bad,0,bad.Length);
            Expect(router,new string[]{FixtureRaw(root,"截断.baobao-toy.png",bad)},DropKind.Rejected,null,null);
            bad=new byte[packaged.Length+1];Buffer.BlockCopy(packaged,0,bad,0,packaged.Length);bad[bad.Length-1]=42;
            Expect(router,new string[]{FixtureRaw(root,"尾垃圾.baobao-toy.png",bad)},DropKind.Rejected,null,"格式损坏");
            bad=(byte[])packaged.Clone();WriteBigEndian(bad,8,UInt32.MaxValue);
            Expect(router,new string[]{FixtureRaw(root,"畸形长度.baobao-toy.png",bad)},DropKind.Rejected,null,"格式损坏");
            bad=(byte[])packaged.Clone();PngPart text=Parts(bad).Find(delegate(PngPart p){return p.Type=="tEXt"&&TextKeyword(p.Data)=="baobao.toy";});bad[text.CrcStart]^=1;
            Expect(router,new string[]{FixtureRaw(root,"坏文本CRC.baobao-toy.png",bad)},DropKind.Rejected,null,"校验失败");
            bad=(byte[])packaged.Clone();PngPart idat=Parts(bad).Find(delegate(PngPart p){return p.Type=="IDAT";});bad[idat.DataStart]^=1;
            Expect(router,new string[]{FixtureRaw(root,"坏像素块CRC.baobao-toy.png",bad)},DropKind.Rejected,null,"校验失败");
            Expect(router,new string[]{FixtureRaw(root,"重复清单.baobao-toy.png",AddToyText(packaged,GoodTree,false))},DropKind.Rejected,null,"重复清单");
            Expect(router,new string[]{FixtureRaw(root,"缺IDAT.baobao-toy.png",WithoutChunks(packaged,delegate(PngPart p){return p.Type=="IDAT";}))},DropKind.Rejected,null,"格式损坏");
            Expect(router,new string[]{FixtureRaw(root,"缺IHDR.baobao-toy.png",WithoutChunks(packaged,delegate(PngPart p){return p.Type=="IHDR";}))},DropKind.Rejected,null,"格式损坏");
            bad=new byte[packaged.Length-12];Buffer.BlockCopy(packaged,0,bad,0,bad.Length);
            Expect(router,new string[]{FixtureRaw(root,"缺IEND.baobao-toy.png",bad)},DropKind.Rejected,null,"格式损坏");
            bad=ReplaceHeaderWidthWithZero(packaged);
            Expect(router,new string[]{FixtureRaw(root,"畸形IHDR.baobao-toy.png",bad)},DropKind.Rejected,null,"格式损坏");
            Expect(router,new string[]{FixtureRaw(root,"PNG未知版本.baobao-toy.png",AddToyText(plain,"{\"schema\":\"baobao.toy\",\"version\":2,\"kind\":\"scratcher\"}",false))},DropKind.Rejected,null,"版本");
            Expect(router,new string[]{FixtureRaw(root,"PNG未知种类.baobao-toy.png",AddToyText(plain,"{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"not_registered\"}",false))},DropKind.Rejected,null,"种类");
            Expect(router,new string[]{FixtureRaw(root,"PNG缺字段.baobao-toy.png",AddToyText(plain,"{\"schema\":\"baobao.toy\",\"version\":1}",false))},DropKind.Rejected,null,"kind");
            Expect(router,new string[]{FixtureRaw(root,"PNG坏编码.baobao-toy.png",AddToyText(plain,GoodScratcher,true))},DropKind.Rejected,null,"编码无效");
            Expect(router,new string[]{FixtureRaw(root,"PNG空清单.baobao-toy.png",AddToyText(plain,"",false))},DropKind.Rejected,null,"清单为空");
            Expect(router,new string[]{FixtureRaw(root,"PNG清单超限.baobao-toy.png",AddToyText(plain,GoodScratcher+new string(' ',ToyManifestExtension.MaximumBytes),false))},DropKind.Rejected,null,"16 KiB");
            bad=new byte[ToyManifestExtension.MaximumPngBytes+1];Buffer.BlockCopy(packaged,0,bad,0,packaged.Length);
            Expect(router,new string[]{FixtureRaw(root,"PNG图片超限.baobao-toy.png",bad)},DropKind.Rejected,null,"2 MiB");
            Expect(router,new string[]{good,ordinary},DropKind.Rejected,null,"分开投放");
            Expect(router,new string[]{good,tree},DropKind.Rejected,null,"一次只接收一件");
            Expect(router,new string[]{good+":stream"},DropKind.Rejected,null,"路径无效");
            StringBuilder alias=new StringBuilder(32768);
            if(GetShortPathName(good,alias,alias.Capacity)>0)Expect(router,new string[]{alias.ToString()},DropKind.Toy,"toy.scratcher",null);

            string futureJson="{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"tunnel\"}";
            string future=Fixture(root,"未来隧道.baobao-toy.json",futureJson);
            string futurePng=FixtureRaw(root,"未来隧道.baobao-toy.png",AddToyText(plain,futureJson,false));
            Expect(router,new string[]{future},DropKind.Rejected,null,"种类");
            Expect(router,new string[]{futurePng},DropKind.Rejected,null,"种类");
            router.RegisterToyType("tunnel","toy.tunnel","猫隧道");
            DropPlan registered=Expect(router,new string[]{future},DropKind.Toy,"toy.tunnel",null);
            Check(registered.DisplayName=="猫隧道","未来种类由本地代码注册显示名");
            Expect(router,new string[]{futurePng},DropKind.Toy,"toy.tunnel",null);
            bool refused=false;try{router.RegisterToyType("unsafe","files.recycle","不允许");}catch(ArgumentException){refused=true;}
            Check(refused,"玩具白名单不能注册普通回收动作");
        }

        private sealed class PngPart {internal string Type;internal byte[] Data,Raw;internal int DataStart,CrcStart;}
        private static List<PngPart> Parts(byte[] png)
        {
            List<PngPart> result=new List<PngPart>();int offset=8;
            while(offset<png.Length)
            {
                int length=(int)ReadBigEndian(png,offset);PngPart part=new PngPart();part.Type=Encoding.ASCII.GetString(png,offset+4,4);
                part.Data=new byte[length];Buffer.BlockCopy(png,offset+8,part.Data,0,length);part.Raw=new byte[length+12];Buffer.BlockCopy(png,offset,part.Raw,0,length+12);
                part.DataStart=offset+8;part.CrcStart=offset+8+length;result.Add(part);offset+=length+12;
            }
            return result;
        }
        private static string TextKeyword(byte[] bytes){int end=Array.IndexOf(bytes,(byte)0);return end<0?"":Encoding.ASCII.GetString(bytes,0,end);}
        private static byte[] WithoutChunks(byte[] png,Predicate<PngPart> remove)
        {
            using(MemoryStream stream=new MemoryStream())
            {stream.Write(png,0,8);foreach(PngPart part in Parts(png))if(!remove(part))stream.Write(part.Raw,0,part.Raw.Length);return stream.ToArray();}
        }
        private static byte[] ReplaceHeaderWidthWithZero(byte[] png)
        {
            using(MemoryStream stream=new MemoryStream())
            {
                stream.Write(png,0,8);foreach(PngPart part in Parts(png))
                {byte[] bytes=part.Raw;if(part.Type=="IHDR"){byte[] data=(byte[])part.Data.Clone();WriteBigEndian(data,0,0);bytes=MakeChunk("IHDR",data);}stream.Write(bytes,0,bytes.Length);}return stream.ToArray();
            }
        }
        private static byte[] AddToyText(byte[] png,string json,bool rawUtf8)
        {
            string text=json;
            if(!rawUtf8){StringBuilder ascii=new StringBuilder();foreach(char c in json){if(c>127)ascii.Append("\\u").Append(((int)c).ToString("x4"));else ascii.Append(c);}text=ascii.ToString();}
            byte[] data=(rawUtf8?Encoding.UTF8:Encoding.ASCII).GetBytes("baobao.toy\0"+text),chunk=MakeChunk("tEXt",data);
            byte[] result=new byte[png.Length+chunk.Length];Buffer.BlockCopy(png,0,result,0,png.Length-12);Buffer.BlockCopy(chunk,0,result,png.Length-12,chunk.Length);Buffer.BlockCopy(png,png.Length-12,result,png.Length-12+chunk.Length,12);return result;
        }
        private static byte[] MakeChunk(string type,byte[] data)
        {
            byte[] bytes=new byte[data.Length+12];WriteBigEndian(bytes,0,(uint)data.Length);Buffer.BlockCopy(Encoding.ASCII.GetBytes(type),0,bytes,4,4);Buffer.BlockCopy(data,0,bytes,8,data.Length);WriteBigEndian(bytes,8+data.Length,Crc32(bytes,4,4+data.Length));return bytes;
        }
        private static uint Crc32(byte[] bytes,int offset,int length)
        {uint crc=0xffffffff;for(int i=offset;i<offset+length;i++){crc^=bytes[i];for(int n=0;n<8;n++)crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1;}return crc^0xffffffff;}
        private static uint ReadBigEndian(byte[] bytes,int offset){return((uint)bytes[offset]<<24)|((uint)bytes[offset+1]<<16)|((uint)bytes[offset+2]<<8)|bytes[offset+3];}
        private static void WriteBigEndian(byte[] bytes,int offset,uint value){bytes[offset]=(byte)(value>>24);bytes[offset+1]=(byte)(value>>16);bytes[offset+2]=(byte)(value>>8);bytes[offset+3]=(byte)value;}
        private static string FixtureRaw(string root,string name,byte[] bytes){string path=Path.Combine(root,name);FixtureBytes(path,bytes);return path;}

        private static void TestExtensionOrder(string root,string toy,string ordinary)
        {
            DropRouter guarded=new DropRouter();TestExtension thief=new TestExtension("","toy.thief");guarded.Register(thief);
            Expect(guarded,new string[]{toy},DropKind.Toy,"toy.scratcher",null);
            Check(thief.PlanCalls==0,"默认专用玩具扩展优先于后注册的通用扩展");
            string custom=Fixture(root,"custom.custom-item","custom interaction fixture");
            DropRouter ordered=new DropRouter();TestExtension first=new TestExtension(".custom-item","interaction.first"),second=new TestExtension(".custom-item","interaction.second");
            ordered.Register(first);ordered.Register(second);
            string[] caller=new string[]{custom};first.MutateInput=true;
            DropPlan result=Expect(ordered,caller,DropKind.Toy,"interaction.first",null);
            Check(first.PlanCalls==1 && second.PlanCalls==0,"新增扩展按注册顺序且先于普通兜底");
            Check(caller[0]==custom && result.Paths[0]==custom,"扩展不能改变调用方数组或替换投放目标");
            first.ReturnedPlan.Paths[0]="changed-after-return";
            Check(result.Paths[0]==custom,"返回计划的路径数组不与扩展对象共享");
            Expect(ordered,new string[]{custom,ordinary},DropKind.Rejected,null,"分开投放");
            Check(first.PlanCalls==1,"混合交互类型拒收时不执行扩展Plan");

            DropRouter unsafeFallback=new DropRouter();TestExtension unsafeExtension=new TestExtension(".custom-item","interaction.unsafe");unsafeExtension.ReturnKind=DropKind.OrdinaryFiles;unsafeFallback.Register(unsafeExtension);
            Expect(unsafeFallback,new string[]{custom},DropKind.Rejected,null,"不能转入普通");
            DropRouter throwing=new DropRouter();TestExtension throwsPlan=new TestExtension(".custom-item","interaction.throw");throwsPlan.ThrowPlan=true;throwing.Register(throwsPlan);
            Expect(throwing,new string[]{custom},DropKind.Rejected,null,"保持原位");
            DropRouter badDetection=new DropRouter();TestExtension throwsCandidate=new TestExtension(".custom-item","interaction.throw");throwsCandidate.ThrowCandidate=true;badDetection.Register(throwsCandidate);
            Expect(badDetection,new string[]{custom},DropKind.Rejected,null,"保持原位");
            DropRouter nullPlan=new DropRouter();TestExtension missing=new TestExtension(".custom-item","interaction.none");missing.NullPlan=true;nullPlan.Register(missing);
            Expect(nullPlan,new string[]{custom},DropKind.Rejected,null,null);
            DropRouter invalidId=new DropRouter();invalidId.Register(new TestExtension(".custom-item","https://not-an-action.invalid"));
            Expect(invalidId,new string[]{custom},DropKind.Rejected,null,"标识无效");
            bool nullRejected=false;try{ordered.Register(null);}catch(ArgumentNullException){nullRejected=true;}
            Check(nullRejected,"空扩展注册明确拒绝");
        }

        private static string Fixture(string root,string name,string text)
        {string path=Path.Combine(root,name);FixtureBytes(path,new UTF8Encoding(false).GetBytes(text));return path;}
        private static void FixtureBytes(string path,byte[] bytes)
        {
            using(FileStream stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.Write(bytes,0,bytes.Length);
            snapshots.Add(path,(byte[])bytes.Clone());attributes.Add(path,File.GetAttributes(path));
        }
        private static DropPlan Expect(DropRouter router,string[] paths,DropKind expected,string action,string message)
        {
            DropPlan plan=router.Plan(paths);
            Check(plan!=null && plan.Kind==expected,"route "+expected+" | "+(paths==null||paths.Length==0?"<empty>":Path.GetFileName(paths[0]))+" | "+(plan==null?"<null>":plan.Message));
            if(action!=null)Check(plan.ActionId==action,"action "+action);
            if(message!=null)Check(plan.Message!=null&&plan.Message.IndexOf(message,StringComparison.Ordinal)>=0,"message contains "+message);
            if(paths!=null)Check(!Object.ReferenceEquals(paths,plan.Paths),"input/output arrays are isolated");
            return plan;
        }
        private static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}

        private sealed class TestExtension : IDropInteractionExtension
        {
            private readonly string suffix,action;
            internal int PlanCalls;
            internal bool MutateInput,ThrowPlan,ThrowCandidate,NullPlan;
            internal DropKind ReturnKind=DropKind.Toy;
            internal DropPlan ReturnedPlan;
            internal TestExtension(string suffix,string action){this.suffix=suffix;this.action=action;}
            public bool IsCandidate(string path){if(ThrowCandidate)throw new Exception("test candidate failure");return path.EndsWith(suffix,StringComparison.OrdinalIgnoreCase);}
            public DropPlan Plan(string[] paths)
            {
                PlanCalls++;if(ThrowPlan)throw new Exception("test plan failure");if(NullPlan)return null;
                if(MutateInput)paths[0]="extension-only-array-change";
                ReturnedPlan=new DropPlan {Kind=ReturnKind,Paths=paths,ActionId=action,DisplayName="测试扩展",Message="测试交互，文件保留。"};return ReturnedPlan;
            }
        }

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern uint GetShortPathName(string longName,StringBuilder shortName,int size);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateSymbolicLinkW",ExactSpelling=true)]
        [return:MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link,string target,uint flags);
    }
}
