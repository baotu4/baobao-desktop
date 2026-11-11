using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace BaobaoDesktop
{
    public enum DropKind { OrdinaryFiles, Toy, Rejected }

    public sealed class DropPlan
    {
        public DropKind Kind;
        public string[] Paths;
        public string ActionId;
        public string DisplayName;
        public string Message;
    }

    public interface IDropInteractionExtension
    {
        bool IsCandidate(string path);
        DropPlan Plan(string[] paths);
    }

    /// <summary>
    /// Read-only routing only. OrdinaryFiles must still pass RecycleService's
    /// independent checks in the caller. Recognized extensions never fall back
    /// to recycling, including when their parser/implementation fails.
    /// </summary>
    public sealed class DropRouter
    {
        private readonly List<IDropInteractionExtension> extensions = new List<IDropInteractionExtension>();
        private readonly ToyManifestExtension toyExtension;

        public DropRouter() { toyExtension=new ToyManifestExtension();extensions.Add(toyExtension); }

        /// <summary>Only trusted local application code can register new kinds.</summary>
        public void RegisterToyType(string kind,string actionId,string displayName)
        { toyExtension.RegisterToyType(kind,actionId,displayName); }

        /// <summary>
        /// Default toy manifests keep highest priority. Additional extensions
        /// run in registration order, all before the ordinary-file fallback.
        /// Call on the same UI thread as Plan.
        /// </summary>
        public void Register(IDropInteractionExtension extension)
        {
            if (extension == null) throw new ArgumentNullException("extension");
            extensions.Add(extension);
        }

        public DropPlan Plan(string[] paths)
        {
            string[] input = paths == null ? new string[0] : (string[])paths.Clone();
            if (input.Length == 0) return DropPathRules.Reject(input,"没有收到文件。");
            // Resolve existing short-name aliases before extension matching.
            // A .baobao-toy.json reached by an 8.3 alias must remain a toy.
            for (int i=0;i<input.Length;i++)
            {
                if (String.IsNullOrWhiteSpace(input[i])) return DropPathRules.Reject(input,"投放中包含空文件路径，请重新选择。");
                input[i]=DropPathRules.CanonicalizeExisting(input[i]);
            }

            IDropInteractionExtension[] snapshot = extensions.ToArray();
            foreach (IDropInteractionExtension extension in snapshot)
            {
                bool any=false,all=true;
                try
                {
                    foreach (string path in input)
                    {
                        bool candidate=extension.IsCandidate(path);
                        any|=candidate; all&=candidate;
                    }
                }
                catch (Exception)
                {
                    return DropPathRules.Reject(input,"无法安全识别投放类型，文件保持原位。");
                }
                if (!any) continue;
                if (!all)
                    return DropPathRules.Reject(input,extension is ToyManifestExtension ?
                        "玩具文件请与普通文件分开投放，所有文件保持原位。" :
                        "不同交互类型的文件请分开投放，所有文件保持原位。");
                try
                {
                    DropPlan planned=extension.Plan((string[])input.Clone());
                    if (planned == null || (planned.Kind != DropKind.Toy && planned.Kind != DropKind.Rejected))
                        return DropPathRules.Reject(input,"已识别的交互文件不能转入普通文件回收，文件保持原位。");
                    if (planned.Kind == DropKind.Toy && !DropPathRules.ValidActionId(planned.ActionId))
                        return DropPathRules.Reject(input,"交互类型标识无效，文件保持原位。");
                    // Extension objects cannot retain/mutate the caller's array,
                    // replace the input targets, or alter a returned plan later.
                    return new DropPlan {
                        Kind=planned.Kind,Paths=(string[])input.Clone(),ActionId=planned.ActionId,
                        DisplayName=planned.DisplayName ?? (planned.Kind==DropKind.Toy ? "玩具" : "未接收"),
                        Message=String.IsNullOrEmpty(planned.Message) ? "文件保持原位。" : planned.Message
                    };
                }
                catch (Exception)
                {
                    return DropPathRules.Reject(input,"交互文件无法读取或格式不受支持，文件保持原位。");
                }
            }
            return new DropPlan {
                Kind=DropKind.OrdinaryFiles,Paths=(string[])input.Clone(),ActionId="files.recycle",
                DisplayName=input.Length==1 ? "普通文件" : input.Length.ToString(CultureInfo.InvariantCulture)+"个普通文件",
                Message="普通文件需要通过回收校验后才能移入系统回收站。"
            };
        }
    }

    /// <summary>
    /// Declarative, local, read-only toy manifests. No scripts, external
    /// resources, commands, reflection-based deserialization or file mutation.
    /// </summary>
    public sealed class ToyManifestExtension : IDropInteractionExtension
    {
        public const int MaximumBytes=16*1024;
        public const int MaximumPngBytes=2*1024*1024;
        public const string FileSuffix=".baobao-toy.json";
        public const string ImageFileSuffix=".baobao-toy.png";
        private static readonly HashSet<string> AllowedFields = new HashSet<string>(new string[] {"schema","version","kind","displayName"},StringComparer.Ordinal);
        private static readonly uint[] PngCrcTable=MakeCrcTable();
        private readonly Dictionary<string,ToyType> toyTypes=new Dictionary<string,ToyType>(StringComparer.Ordinal);

        public ToyManifestExtension()
        {
            RegisterToyType("scratcher","toy.scratcher","猫抓板");
            RegisterToyType("cat_tree","toy.cat_tree","猫爬架");
        }

        public void RegisterToyType(string kind,string actionId,string displayName)
        {
            if(!DropPathRules.ValidToyKind(kind))throw new ArgumentException("玩具种类标识无效。","kind");
            if(!DropPathRules.ValidActionId(actionId)||actionId.StartsWith("files.",StringComparison.Ordinal))throw new ArgumentException("玩具动作标识无效。","actionId");
            if(!DropPathRules.ValidDisplayName(displayName))throw new ArgumentException("玩具显示名称无效。","displayName");
            if(toyTypes.ContainsKey(kind))throw new ArgumentException("这个玩具种类已经注册。","kind");
            toyTypes.Add(kind,new ToyType {ActionId=actionId,DisplayName=displayName.Trim()});
        }

        public bool IsCandidate(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string candidate=DropPathRules.CanonicalizeExisting(path).Replace('/','\\').TrimEnd('\\',' ', '\t', '.');
            int slash=candidate.LastIndexOf('\\');
            string name=slash<0?candidate:candidate.Substring(slash+1);
            int stream=name.IndexOf(':');
            if (stream>=0) name=name.Substring(0,stream);
            return name.EndsWith(FileSuffix,StringComparison.OrdinalIgnoreCase) || name.EndsWith(ImageFileSuffix,StringComparison.OrdinalIgnoreCase);
        }

        public DropPlan Plan(string[] paths)
        {
            string[] input=paths==null?new string[0]:(string[])paths.Clone();
            if (input.Length != 1)
                return DropPathRules.Reject(input,"一次只接收一件玩具，请分开投放；所有文件保持原位。");
            if (!IsCandidate(input[0])) return DropPathRules.Reject(input,"这不是受支持的包包玩具清单，文件保持原位。");
            string path,error=DropPathRules.ValidateToyPath(input[0],out path);
            if (error!=null) return DropPathRules.Reject(input,error);
            input[0]=path;
            try
            {
                bool isPng=path.EndsWith(ImageFileSuffix,StringComparison.OrdinalIgnoreCase);
                byte[] fileData=ReadBounded(path,isPng?MaximumPngBytes:MaximumBytes,isPng?
                    "玩具图片不能超过2 MiB，文件保持原位。":"玩具清单不能超过16 KiB，文件保持原位。");
                byte[] data=isPng?ReadPngManifest(fileData):fileData;
                string json=new UTF8Encoding(false,true).GetString(data);
                if (json.Length>0 && json[0]=='\ufeff') json=json.Substring(1);
                EnsureSingleJsonObject(json);
                data=Encoding.UTF8.GetBytes(json);
                Dictionary<string,JsonValue> values=ReadDeclaration(data);
                JsonValue schema,version,kind,name;
                if (!values.TryGetValue("schema",out schema) || schema.Type!="string" || schema.Text!="baobao.toy")
                    return DropPathRules.Reject(input,"玩具清单的 schema 缺失或不受支持，文件保持原位。");
                if (!values.TryGetValue("version",out version) || version.Type!="number")
                    return DropPathRules.Reject(input,"玩具清单的 version 必须是整数，文件保持原位。");
                int versionNumber;
                string versionText=version.Text.Trim();
                if (!Int32.TryParse(versionText,NumberStyles.None,CultureInfo.InvariantCulture,out versionNumber) ||
                    versionNumber.ToString(CultureInfo.InvariantCulture)!=versionText)
                    return DropPathRules.Reject(input,"玩具清单的 version 必须是整数，文件保持原位。");
                if (versionNumber!=1)
                    return DropPathRules.Reject(input,"暂不支持这个玩具清单版本，目前仅支持 version=1；文件保持原位。");
                if (!values.TryGetValue("kind",out kind) || kind.Type!="string")
                    return DropPathRules.Reject(input,"玩具清单缺少有效的 kind，文件保持原位。");
                ToyType toyType;
                if(!toyTypes.TryGetValue(kind.Text,out toyType))return DropPathRules.Reject(input,"暂不支持这个玩具种类，文件保持原位。");
                string displayName=toyType.DisplayName;
                if (values.TryGetValue("displayName",out name))
                {
                    if (name.Type!="string" || !DropPathRules.ValidDisplayName(name.Text))
                        return DropPathRules.Reject(input,"玩具名称须为1至64个可读字符，文件保持原位。");
                    displayName=name.Text.Trim();
                }
                return new DropPlan {
                    Kind=DropKind.Toy,Paths=(string[])input.Clone(),ActionId=toyType.ActionId,
                    DisplayName=displayName,
                    Message="已识别"+displayName+"，玩具文件已保留。"
                };
            }
            catch (ManifestException e) { return DropPathRules.Reject(input,e.Message); }
            catch (DecoderFallbackException) { return DropPathRules.Reject(input,"玩具清单需要使用有效的 UTF-8 编码，文件保持原位。"); }
            catch (UnauthorizedAccessException) { return DropPathRules.Reject(input,"没有读取这个玩具清单的权限，文件保持原位。"); }
            catch (IOException) { return DropPathRules.Reject(input,"玩具清单无法读取或正在被占用，文件保持原位。"); }
            catch (Exception) { return DropPathRules.Reject(input,"玩具清单格式损坏或层级过深，文件保持原位。"); }
        }

        private static byte[] ReadBounded(string path,int maximum,string sizeMessage)
        {
            using (FileStream stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if (stream.Length>maximum) throw new ManifestException(sizeMessage);
                byte[] buffer=new byte[maximum+1];
                int used=0,read;
                while (used<buffer.Length && (read=stream.Read(buffer,used,buffer.Length-used))>0) used+=read;
                if (used>maximum) throw new ManifestException(sizeMessage);
                byte[] result=new byte[used];Buffer.BlockCopy(buffer,0,result,0,used);return result;
            }
        }

        private static byte[] ReadPngManifest(byte[] png)
        {
            byte[] signature=new byte[] {137,80,78,71,13,10,26,10};
            if(png.Length<signature.Length)throw BadPng();
            for(int i=0;i<signature.Length;i++)if(png[i]!=signature[i])throw BadPng();
            bool header=false,idat=false,idatClosed=false,end=false,palette=false;
            uint colorType=0;long idatBytes=0;int offset=8;byte[] manifest=null;
            while(offset<png.Length)
            {
                if(png.Length-offset<12)throw BadPng();
                uint unsignedLength=ReadBigEndian(png,offset);
                if(unsignedLength>(uint)(png.Length-offset-12))throw BadPng();
                int length=(int)unsignedLength,dataStart=offset+8,chunkEnd=dataStart+length+4;
                for(int i=0;i<4;i++)if(!AsciiLetter(png[offset+4+i]))throw BadPng();
                if((png[offset+6]&32)!=0)throw BadPng();
                string type=Encoding.ASCII.GetString(png,offset+4,4);
                uint crc=0xffffffff;
                for(int i=offset+4;i<dataStart+length;i++)crc=PngCrcTable[(crc^png[i])&255]^(crc>>8);
                if((crc^0xffffffff)!=ReadBigEndian(png,dataStart+length))
                    throw new ManifestException("玩具图片校验失败，文件保持原位。");
                if(!header && type!="IHDR")throw BadPng();
                if(idat && type!="IDAT")idatClosed=true;
                if(type=="IHDR")
                {
                    if(header||offset!=8||length!=13)throw BadPng();
                    uint width=ReadBigEndian(png,dataStart),height=ReadBigEndian(png,dataStart+4);
                    int depth=png[dataStart+8];colorType=png[dataStart+9];
                    bool depthValid=(colorType==0&&(depth==1||depth==2||depth==4||depth==8||depth==16))||
                        (colorType==2&&(depth==8||depth==16))||(colorType==3&&(depth==1||depth==2||depth==4||depth==8))||
                        ((colorType==4||colorType==6)&&(depth==8||depth==16));
                    if(width==0||height==0||width>Int32.MaxValue||height>Int32.MaxValue||!depthValid||
                        png[dataStart+10]!=0||png[dataStart+11]!=0||png[dataStart+12]>1)throw BadPng();
                    header=true;
                }
                else if(type=="PLTE")
                {
                    if(palette||idat||length<3||length>768||length%3!=0)throw BadPng();
                    palette=true;
                }
                else if(type=="IDAT")
                {
                    if(idatClosed||(colorType==3&&!palette))throw BadPng();
                    idat=true;idatBytes+=length;
                }
                else if(type=="IEND")
                {
                    if(end||length!=0||!idat||idatBytes<6||chunkEnd!=png.Length)throw BadPng();
                    end=true;offset=chunkEnd;break;
                }
                else if(type=="tEXt")
                {
                    int keywordEnd=-1;
                    for(int i=0;i<length;i++)if(png[dataStart+i]==0){keywordEnd=i;break;}
                    if(keywordEnd<1||keywordEnd>79)throw BadPng();
                    string keyword=Encoding.ASCII.GetString(png,dataStart,keywordEnd);
                    if(keyword=="baobao.toy")
                    {
                        if(manifest!=null)throw new ManifestException("玩具图片包含重复清单，文件保持原位。");
                        int manifestLength=length-keywordEnd-1;
                        if(manifestLength<1||manifestLength>MaximumBytes)
                            throw new ManifestException("图片内的玩具清单为空或超过16 KiB，文件保持原位。");
                        manifest=new byte[manifestLength];
                        Buffer.BlockCopy(png,dataStart+keywordEnd+1,manifest,0,manifestLength);
                        foreach(byte value in manifest)if(value>127)
                            throw new ManifestException("图片内的玩具清单编码无效，文件保持原位。");
                    }
                }
                else if(png[offset+4]>='A'&&png[offset+4]<='Z')throw BadPng();
                offset=chunkEnd;
            }
            if(!header||!idat||!end||offset!=png.Length)throw BadPng();
            if(manifest==null)throw new ManifestException("图片没有有效的包包玩具清单，文件保持原位。");
            return manifest;
        }

        private static ManifestException BadPng() {return new ManifestException("玩具图片格式损坏或不完整，文件保持原位。");}
        private static bool AsciiLetter(byte c){return(c>='A'&&c<='Z')||(c>='a'&&c<='z');}
        private static uint ReadBigEndian(byte[] bytes,int offset)
        {return((uint)bytes[offset]<<24)|((uint)bytes[offset+1]<<16)|((uint)bytes[offset+2]<<8)|bytes[offset+3];}
        private static uint[] MakeCrcTable()
        {
            uint[] table=new uint[256];
            for(uint n=0;n<256;n++){uint c=n;for(int i=0;i<8;i++)c=(c&1)!=0?0xedb88320^(c>>1):c>>1;table[n]=c;}
            return table;
        }

        private static void EnsureSingleJsonObject(string json)
        {
            // The built-in WCF JSON reader is deliberately permissive and can
            // merge consecutive JSON objects into one XML root. Check document
            // boundaries and trailing commas before using that reader. String
            // escapes remain the responsibility of the real JSON reader.
            int first=0;
            while(first<json.Length && JsonWhitespace(json[first])) first++;
            if(first==json.Length || json[first]!='{')
                throw new ManifestException("玩具清单的最外层必须是单个 JSON 对象，文件保持原位。");
            Stack<char> nesting=new Stack<char>();
            bool quoted=false,escaped=false,afterComma=false;
            for(int i=first;i<json.Length;i++)
            {
                char c=json[i];
                if(quoted)
                {
                    if(c<32)throw new ManifestException("玩具清单包含无效的控制字符，文件保持原位。");
                    if(escaped)escaped=false;
                    else if(c=='\\')escaped=true;
                    else if(c=='\"')quoted=false;
                    continue;
                }
                if(JsonWhitespace(c))continue;
                if(c=='\"'){quoted=true;afterComma=false;continue;}
                if(c=='{'||c=='[')
                {
                    nesting.Push(c);afterComma=false;
                    if(nesting.Count>8)throw new ManifestException("玩具清单层级过深，文件保持原位。");
                    continue;
                }
                if(c=='}'||c==']')
                {
                    if(afterComma || nesting.Count==0 || (c=='}'&&nesting.Pop()!='{') || (c==']'&&nesting.Pop()!='['))
                        throw new ManifestException("玩具清单格式损坏，文件保持原位。");
                    afterComma=false;
                    if(nesting.Count==0)
                    {
                        for(int rest=i+1;rest<json.Length;rest++)
                            if(!JsonWhitespace(json[rest]))throw new ManifestException("玩具清单只能包含一个 JSON 对象，文件保持原位。");
                        return;
                    }
                    continue;
                }
                if(c==',')
                {
                    if(afterComma)throw new ManifestException("玩具清单格式损坏，文件保持原位。");
                    afterComma=true;
                }
                else afterComma=false;
            }
            throw new ManifestException("玩具清单格式不完整，文件保持原位。");
        }

        private static bool JsonWhitespace(char c) {return c==' '||c=='\t'||c=='\r'||c=='\n';}

        private static Dictionary<string,JsonValue> ReadDeclaration(byte[] data)
        {
            XmlDictionaryReaderQuotas quotas=new XmlDictionaryReaderQuotas();
            quotas.MaxDepth=8;quotas.MaxStringContentLength=MaximumBytes;
            quotas.MaxArrayLength=MaximumBytes;quotas.MaxBytesPerRead=4096;quotas.MaxNameTableCharCount=4096;
            XmlDocument document=new XmlDocument();document.XmlResolver=null;
            using (XmlDictionaryReader reader=JsonReaderWriterFactory.CreateJsonReader(data,quotas)) document.Load(reader);
            XmlElement root=document.DocumentElement;
            if (root==null || root.LocalName!="root" || root.GetAttribute("type")!="object")
                throw new ManifestException("玩具清单的最外层必须是 JSON 对象，文件保持原位。");
            CheckAttributes(root);
            Dictionary<string,JsonValue> result=new Dictionary<string,JsonValue>(StringComparer.Ordinal);
            foreach(XmlNode child in root.ChildNodes)
            {
                XmlElement member=child as XmlElement;
                if (member==null)
                {
                    if (!String.IsNullOrWhiteSpace(child.InnerText)) throw new ManifestException("玩具清单格式无效，文件保持原位。");
                    continue;
                }
                string field=member.LocalName;
                if (!AllowedFields.Contains(field))
                    throw new ManifestException("玩具清单含有不支持的字段；外链、命令和脚本不会执行，文件保持原位。");
                if (result.ContainsKey(field)) throw new ManifestException("玩具清单包含重复字段，文件保持原位。");
                CheckAttributes(member);
                string type=member.GetAttribute("type");if(type.Length==0)type="string";
                if (type!="string" && type!="number") throw new ManifestException("玩具清单仅允许简单声明字段，文件保持原位。");
                foreach(XmlNode valueChild in member.ChildNodes)
                    if (valueChild is XmlElement) throw new ManifestException("玩具清单不能包含嵌套配置，文件保持原位。");
                result.Add(field,new JsonValue {Type=type,Text=member.InnerText});
            }
            return result;
        }

        private static void CheckAttributes(XmlElement element)
        {
            foreach(XmlAttribute attribute in element.Attributes)
                if (attribute.Name!="type" || attribute.NamespaceURI.Length!=0)
                    throw new ManifestException("玩具清单包含不支持的元数据，文件保持原位。");
        }

        private sealed class JsonValue { internal string Type;internal string Text; }
        private sealed class ToyType { internal string ActionId;internal string DisplayName; }
        private sealed class ManifestException : Exception { internal ManifestException(string message):base(message){} }
    }

    internal static class DropPathRules
    {
        internal static DropPlan Reject(string[] paths,string message)
        {
            return new DropPlan {Kind=DropKind.Rejected,Paths=paths==null?new string[0]:(string[])paths.Clone(),ActionId=null,DisplayName="未接收",Message=message};
        }

        internal static string CanonicalizeExisting(string path)
        {
            if (!LocalAbsolute(path) || path.IndexOf(':',2)>=0 || AmbiguousSegments(path)) return path;
            try
            {
                string full=System.IO.Path.GetFullPath(path).Replace('/','\\');
                StringBuilder longName=new StringBuilder(32768);
                uint length=GetLongPathName(full,longName,longName.Capacity);
                return length>0 && length<longName.Capacity?longName.ToString():full;
            }
            catch(Exception) {return path;}
        }

        internal static string ValidateToyPath(string path,out string full)
        {
            full=null;
            if (!LocalAbsolute(path)) return "玩具清单必须是本地绝对文件路径，文件保持原位。";
            if (path.IndexOf(':',2)>=0 || path.IndexOf('*')>=0 || path.IndexOf('?')>=0 || path.IndexOf('\0')>=0 || AmbiguousSegments(path) || path.EndsWith("\\",StringComparison.Ordinal) || path.EndsWith("/",StringComparison.Ordinal))
                return "玩具清单路径无效，文件保持原位。";
            try
            {
                full=CanonicalizeExisting(path);
                string root=System.IO.Path.GetPathRoot(full);
                DriveInfo drive=new DriveInfo(root);
                if (!drive.IsReady || drive.DriveType==DriveType.Network || drive.DriveType==DriveType.Unknown || drive.DriveType==DriveType.NoRootDirectory)
                    return "玩具清单必须来自可读取的本地磁盘，文件保持原位。";
                string current=root;
                string[] components=full.Substring(root.Length).Split('\\');
                for(int i=0;i<components.Length;i++)
                {
                    if (components[i].Length==0) return "玩具清单路径无效，文件保持原位。";
                    current=System.IO.Path.Combine(current,components[i]);
                    FileAttributes attributes=File.GetAttributes(current);
                    if ((attributes&FileAttributes.ReparsePoint)!=0)
                        return "玩具清单不能通过符号链接或重解析路径读取，文件保持原位。";
                    if (i==components.Length-1 && (attributes&FileAttributes.Directory)!=0)
                        return "玩具清单必须是普通文件，不能是文件夹。";
                    if (i<components.Length-1 && (attributes&FileAttributes.Directory)==0)
                        return "玩具清单的上级路径不是文件夹，文件保持原位。";
                }
                return null;
            }
            catch(FileNotFoundException) {return "玩具清单不存在，未执行任何操作。";}
            catch(DirectoryNotFoundException) {return "玩具清单路径不存在，未执行任何操作。";}
            catch(UnauthorizedAccessException) {return "没有读取这个玩具清单的权限，文件保持原位。";}
            catch(Exception) {return "无法确认玩具清单是可读取的本地普通文件，文件保持原位。";}
        }

        private static bool LocalAbsolute(string path)
        {
            return !String.IsNullOrWhiteSpace(path) && path.Length>=3 &&
                ((path[0]>='a'&&path[0]<='z')||(path[0]>='A'&&path[0]<='Z')) && path[1]==':' && (path[2]=='\\'||path[2]=='/');
        }

        private static bool AmbiguousSegments(string path)
        {
            if (path==null || path.Length<3) return true;
            foreach(string part in path.Replace('/','\\').Substring(3).Split('\\'))
                if (part.Length>0 && part!="." && part!=".." && (part.EndsWith(" ",StringComparison.Ordinal)||part.EndsWith(".",StringComparison.Ordinal))) return true;
            return false;
        }

        internal static bool ValidActionId(string id)
        {
            if (String.IsNullOrEmpty(id)||id.Length>64) return false;
            foreach(char c in id) if (!((c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='.'||c=='_'||c=='-')) return false;
            return id[0]>='a'&&id[0]<='z';
        }

        internal static bool ValidToyKind(string kind)
        {
            if(String.IsNullOrEmpty(kind)||kind.Length>48||kind[0]<'a'||kind[0]>'z')return false;
            foreach(char c in kind)if(!((c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'))return false;
            return true;
        }

        internal static bool ValidDisplayName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)||name.Trim().Length>64) return false;
            foreach(char c in name) if (Char.IsControl(c)) return false;
            return true;
        }

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern uint GetLongPathName(string shortName,StringBuilder longName,int bufferLength);
    }
}
