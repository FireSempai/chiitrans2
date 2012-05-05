﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Net;
using System.IO;
using System.Web;
using System.Threading;
using System.Windows.Forms;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Globalization;

namespace ChiiTrans
{
    class Translation
    {
        public static HashSet<Translation> current = new HashSet<Translation>();
        private static int transId = 0;
        public static string lastGoodBuffer = "";
        
        private string source;
        private string sourceNew;
        private string sourceFixed;
        public int id { get; private set; }
        public static string[] Translators = { "Atlas", "OCN", "Babylon", "Google", "SysTran", "Excite", "SDL", "Microsoft", "Honyaku", "BabelFish", "Translit (int.)", "Translit (Google)", "Translit (MeCab)", "Hivemind (alpha)", "EDICT" };
        private int tasksToComplete;
        private Options options;

        private string lastParsedSource = "";
        private string lastParsedResult;
        
        public Translation(int id, string source, Options _options)
        {
            options = _options;

            this.id = id;
            this.source = source;
            this.sourceFixed = makeReplacements(source);
            if (options.excludeSpeakers)
                this.sourceNew = excludeSpeaker(sourceFixed);
            else
                this.sourceNew = sourceFixed;
            sourceNew = makeFinalAdjustments(sourceNew);
            List<string> usedTranslators = new List<string>();
            foreach (TranslatorRecord rec in options.translators)
            {
                if (rec.inUse)
                {
                    string trans = Translators[rec.id];
                    if (trans == "Atlas" && !Atlas.Ready())
                        continue;
                    if (trans == "Translit (MeCab)" && !Mecab.Ready())
                        continue;
                    usedTranslators.Add(Translators[rec.id]);
                }
            }
            List<object> args = new List<object>();
            args.Add(id);
            bool parseWords = options.displayOriginal && (options.wordParseMethod == Options.PARSE_BUILTIN && Edict.instance.Ready && Inflect.instance.Ready || options.wordParseMethod == Options.PARSE_WWWJDIC);
            bool reserveLineHeight = parseWords && options.displayReadings;
            args.Add(reserveLineHeight);
            args.Add(options.furiganaRomaji);
            if (options.displayOriginal)
            {
                args.Add(source);
                if (options.displayFixed && source != sourceFixed)
                    args.Add(sourceFixed);
                else
                    args.Add("");
            }
            else
            {
                args.Add("");
                args.Add("");
            }
            args.Add(parseWords && options.wordParseMethod == Options.PARSE_BUILTIN && Edict.Created());
            foreach (string name in usedTranslators)
            {
                args.Add(name);
            }
            Global.RunScript2("AddTranslationBlock", args.ToArray());
            if (parseWords)
                tasksToComplete = usedTranslators.Count + 1;
            else
                tasksToComplete = usedTranslators.Count;
            current.Add(this);
            foreach (string s in usedTranslators)
            {
                if (s == "Translit (MeCab)")
                    new TranslationTask(this, s, this.GetType().GetMethod("TranslateMecabTranslit"), false);
                else if (s == "Translit (Google)")
                    new TranslationTask(this, s, this.GetType().GetMethod("TranslateTranslit"), false);
                else if (s == "Translit (int.)")
                    new TranslationTask(this, s, this.GetType().GetMethod("TranslateMyTranslit"), false);
                else if (s.StartsWith("Hivemind"))
                    new TranslationTask(this, s, this.GetType().GetMethod("TranslateHivemind"), false);
                else
                    new TranslationTask(this, s, this.GetType().GetMethod("Translate" + s), false);
            }
            if (parseWords)
            {
                if (options.wordParseMethod == Options.PARSE_BUILTIN)
                {
                    if (Edict.Created())
                    {
                        BuiltinParserLookup();
                        CompleteTask();
                    }
                    else
                    {
                        new TranslationTask(this, "Builtin", this.GetType().GetMethod("BuiltinParserLookup"), true);
                    }
                }
                else
                {
                    new TranslationTask(this, "JDic", this.GetType().GetMethod("JDicLookup"), true);
                }
            }
        }
        
        private string makeReplacements(string source)
        {
            string result = source;
            foreach (int ch in result)
            {
                if (ch >= 0xFF61 && ch <= 0xFF9F)
                {
                    // half-width kana
                    result = HalfWidth.Convert(result);
                    break;
                }
            }
            //full-width numbers
            StringBuilder sb = new StringBuilder();
            foreach (int ch in result)
            {
                if (ch >= 0xFF10 && ch <= 0xFF19)
                {
                    sb.Append((char)('0' + (ch - 0xFF10)));
                }
                else
                {
                    sb.Append((char)ch);
                }
            }
            result = sb.ToString();
            string sempai = null;
            string sensei = null;
            foreach (Replacement rep in options.replacements.OrderByDescending(rep => rep.oldText.Length))
            {
                if (rep.oldText.StartsWith("#"))
                {
                    continue;
                }
                //suffixes hack
                if (rep.oldText == "先輩")
                    sempai = rep.newText;
                else if (rep.oldText == "先生")
                    sensei = rep.newText;
                else
                    result = replaceNormal(result, rep.oldText, rep.newText);
            }
            if (options.replaceSuffixes)
                result = replaceSuffixes(result);
            if (sempai != null)
                result = replaceNormal(result, "先輩", sempai);
            if (sensei != null)
                result = replaceNormal(result, "先生", sensei);
            return result;
        }

        public static bool isKatakana(char ch)
        {
            return (ch >= '\u30A0' && ch <= '\u30FF');
        }

        public static bool isHiragana(char ch)
        {
            return (ch >= '\u3040' && ch <= '\u309F');
        }

        public static bool isKanji(char ch)
        {
            return char.GetUnicodeCategory(ch) == UnicodeCategory.OtherLetter && !isKatakana(ch) && !isHiragana(ch);
        }

        public static bool hasJapanese(string s)
        {
            return s.Any(ch => char.GetUnicodeCategory(ch) == UnicodeCategory.OtherLetter);
        }

        public static string KatakanaToHiragana(string s)
        {
            StringBuilder res = new StringBuilder();
            foreach (char ch in s)
            {
                if (isKatakana(ch) && ch != '・')
                    res.Append((char)(ch - 0x60));
                else
                    res.Append(ch);
            }
            return res.ToString();
        }

        public static string HiraganaToRomaji(string s)
        {
            return HiraganaConvertor.instance.Convert(s);
        }
        
        private string replaceNormal(string source, string oldText, string newText)
        {
            if (oldText.Length == 0)
                return source;
            if (newText.Length > 0)
            {
                newText = newText.Replace(' ', '　');
                UnicodeCategory cat = char.GetUnicodeCategory(newText[newText.Length - 1]);
                if (cat == UnicodeCategory.UppercaseLetter || cat == UnicodeCategory.LowercaseLetter)
                    newText += "　";
                bool allKatakana = true;
                foreach (char ch in oldText)
                {
                    if (!isKatakana(ch))
                    {
                        allKatakana = false;
                    }
                }
                if (allKatakana)
                {
                    return Regex.Replace(source, "(?<![\\u30A1-\\u30FA])" + Regex.Escape(oldText) + "(?![\\u30A1-\\u30FA])", newText);
                }
            }
            return Regex.Replace(source, oldText, newText);
        }

        private string suffixReplace(string source, string to_find, string replacement)
        {
            Regex suf = new Regex("([A-Za-z])　" + to_find);
            return suf.Replace(source, match => match.Groups[1].Value + replacement + "　");
        }
        
        private static readonly string[,] suffixes = {
            {"さん", "-san"},
            {"くん", "-kun"},
            {"クン", "-kun"},
            {"ちゃん", "-chan"},
            {"チャン", "-chan"},
            {"ちん", "-chin"},
            {"せんぱい", "-sempai"},
            {"センパイ", "-sempai"},
            {"先輩", "-sempai"},
            {"先生", "-sensei"},
            {"っち", "cchi"},
            {"様", "-sama"},
            {"氏", "-shi"},
            {"君", "-kun"},
            {"殿", "-dono"}
        };

        private string replaceSuffixes(string source)
        {
            string x = source;
            for (int i = 0; i < suffixes.GetLength(0); ++i)
                x = suffixReplace(x, suffixes[i, 0], suffixes[i, 1]);
            return x;
        }

        private string excludeSpeaker(string source)
        {
            try
            {
                Match match = Regex.Match(source, options.excludeSpeakersPattern);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (Exception) { }
            return source;
        }

        string removeSmallLetters(string source, Match m)
        {
            if (m.Success && m.Index > 0)
            {
                string toRomaji = HiraganaConvertor.instance.ConvertLetter(m.Value[0]);
                string toRomajiPrev = HiraganaConvertor.instance.ConvertLetter(source[m.Index - 1]);
                if (!string.IsNullOrEmpty(toRomajiPrev) && !string.IsNullOrEmpty(toRomaji) && toRomajiPrev[toRomajiPrev.Length - 1] == toRomaji[0])
                    return "";
            }
            return m.Value;
        }
        
        private string makeFinalAdjustments(string source)
        {
            if (source.Length == 0)
                return source;
            source = Regex.Replace(source, @"[っッ～]+\b", "");
            source = Regex.Replace(source, @"(?<=[\u3040-\u309F])ー", "");
            source = Regex.Replace(source, "[ぁぃぅぇぉ]", m => removeSmallLetters(source, m));
            source = source.Replace('『', '「')
                           .Replace('』', '」')
                           .Replace("…", "・・・")
                           .Replace("‥", "・・");
            source = Regex.Replace(source, "・{2,}", "... ");
            return source;
        }

        public void CompleteTask()
        {
            Interlocked.Add(ref tasksToComplete, -1);
            if (tasksToComplete <= 0)
                current.Remove(this);
        }

        public static HttpWebRequest CreateHTTPRequest(string url)
        {
            HttpWebRequest result = (HttpWebRequest)WebRequest.Create(url);
            result.Proxy = null;
            result.UserAgent = "Mozilla/4.0 (compatible; MSIE 7.0; Windows NT 5.1; .NET CLR 2.0.50727; .NET CLR 3.5.30729; .NET CLR 3.0.30729)";
            result.Timeout = 10000;
            return result;
        }

        public static string ReadAnswer(HttpWebRequest req)
        {
            return ReadAnswer(req, Encoding.UTF8);
        }

        public static string ReadAnswer(HttpWebRequest req, Encoding encoding)
        {

            return new StreamReader(req.GetResponse().GetResponseStream(), encoding).ReadToEnd();

        }

        public static void WritePost(HttpWebRequest req, string data)
        {
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.ContentLength = data.Length;
            Stream ss = req.GetRequestStream();
            StreamWriter sw = new StreamWriter(ss);
            sw.Write(data);
            sw.Close();
            ss.Close();
        }
        
        public static string UrlEncode(string data)
        {
            return HttpUtility.UrlEncode(data, Encoding.UTF8);
        }

        private string FindSubString(string source, string beginMark, string endMark)
        {
            int x = source.IndexOf(beginMark);
            if (x < 0)
                return null;
            x += beginMark.Length;
            int y = source.IndexOf(endMark, x);
            if (y < 0)
                return null;
            return source.Substring(x, y - x);
        }
        
        public string TranslateBabylon()
        {

            string src = sourceFixed.Replace('「', '"').Replace('」', '"');
            string url = "http://translation.babylon.com/translate/babylon.php";
            string srclang = "8"; //Japanese
            string destlang = "0"; //English
            //string destlang = "7"; //Russian
            string query1 = "?v=1.0&q=" + UrlEncode(src) + "&langpair="+srclang+"%7C"+destlang+"&callback=babylonTranslator.callback&context=babylon.8.0._babylon_api_response";
            HttpWebRequest req = CreateHTTPRequest(url+query1);
            string reqq=ReadAnswer(req);
            string result;
            result = FindSubString(reqq, "{\"translatedText\":\"", "\"}");
            if (result != null)
                return result.Replace("\\", "");
            else
                return "(Server return ERROR)";
                
            
        }

        public string TranslateSDL()
        {
            string url = "http://tets9.freetranslation.com/";
            string query = "sequence=core&charset=UTF-8&language=Japanese%2FEnglish&srctext=" + UrlEncode(sourceNew);
            HttpWebRequest req = CreateHTTPRequest(url);
            WritePost(req, query);
            return ReadAnswer(req);
        }

        public string TranslateMicrosoft()
        {
            string url = "http://api.microsofttranslator.com/v2/ajax.svc/TranslateArray";
            JsArray src = new JsArray();
            src.Add(new JsAtom(sourceNew));
            string query = "from=%22ja%22&to=%22en%22&appId=%22" + UrlEncode("F84955C82256C25518548EE0C161B0BF87681F2F") + "%22&texts=" + UrlEncode(src.Serialize());
            HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
            string ans = ReadAnswer(req);
            JsObject js = Json.Parse(ans);
            string result = js["0"]["TranslatedText"].ToString();
            return result;
        }

        public string TranslateTranslit()
        {
            string url = "http://translate.google.com/translate_a/t";
            string query = "client=t&text=" + UrlEncode(sourceFixed.Replace('　', ' ')) + "&sl=ja&tl=ja";
            HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
            string ans = ReadAnswer(req);
            JsObject js = Json.Parse(ans);
            string result = js["0"]["0"]["2"].ToString();
            result = Regex.Replace(result, "~tsu ([A-Za-z])", match => match.Groups[1].Value + match.Groups[1].Value);
            result = result.Replace("~tsu", "");
            return result;
        }

        public string TranslateGoogle()  //<AlexSt
        {
            string url = "http://translate.google.com/translate_a/t";
            string srclang = "ja";
            string destlang = "en";
            if (options.translateToOtherLanguage)
            {
                destlang = options.translateLanguage;
                bool allLatin = true;
                foreach (char ch in sourceNew)
                {
                    if (char.IsLetter(ch) && !(ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z'))
                    {
                        allLatin = false;
                        break;
                    }
                }
                if (allLatin)
                {
                    srclang = "en";
                }
                else
                {
                    srclang = "ja";
                }
            }
            else
            {
                srclang = "ja";
                destlang = "en";
            }
            string query = "client=t&text=" + UrlEncode(sourceFixed.Replace('　', ' ')) + "&sl=" + srclang + "&tl=" + destlang;
            HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
            string ans = ReadAnswer(req);
            JsObject js = Json.Parse(ans);
            string output = "";
            foreach (JsObject znach in js["0"])
                output = output + " " + znach["0"].ToString();
            //string result = js["0"]["0"]["0"].ToString();
            string result = output;
            result = Regex.Replace(result, "~tsu ([A-Za-z])", match => match.Groups[1].Value + match.Groups[1].Value);
            result = result.Replace("~tsu", "");
            return result;
        }

        public string OldFuncTranslateGoogle()
        {
            string url = "http://ajax.googleapis.com/ajax/services/language/translate";
            string srclang, destlang;
            if (options.translateToOtherLanguage)
            {
                destlang = options.translateLanguage;
                bool allLatin = true;
                foreach (char ch in sourceNew)
                {
                    if (char.IsLetter(ch) && !(ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z'))
                    {
                        allLatin = false;
                        break;
                    }
                }
                if (allLatin)
                {
                    srclang = "en";
                }
                else
                {
                    srclang = "ja";
                }
            }
            else
            {
                srclang = "ja";
                destlang = "en";
            }
            string query = "v=1.0&q=" + UrlEncode(sourceNew) + "&langpair=" + srclang + "%7C" + destlang;
            HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
            JsObject js = Json.Parse(ReadAnswer(req));
            return js["responseData"].str["translatedText"];
        }

        public string TranslateOCN()
        {
            // Защита сумрачных японских гениев :)
            var req0 = CreateHTTPRequest("http://cgi01.ocn.ne.jp/cgi-bin/translation/counter.cgi?prm=63676930312e6f636e2e6e652e6a70");
            string huita = ReadAnswer(req0);
            var param = Regex.Match(huita, @"'(.*?)'").Groups[1].Value;

            string url = "http://cgi01.ocn.ne.jp/cgi-bin/translation/index.cgi";
            string src = sourceNew.Replace('-', '‐');
            string query = "langpair=jaen&sourceText=" + UrlEncode(src) + "&auth=" + param;
            HttpWebRequest req = CreateHTTPRequest(url);
            WritePost(req, query);
            string result = ReadAnswer(req, Encoding.GetEncoding(932));
            Regex re = new Regex("NAME=\"responseText\".*?\\>(.*?)\\<\\/TEXTAREA\\>", RegexOptions.Singleline);
            Match m = re.Match(result);
            if (m.Success)
                return m.Groups[1].Value;
            else
                throw new Exception();
        }

        public string TranslateHonyaku()
        {
            string url = "http://honyaku.yahoo.co.jp/transtext";
            string query = "both=TH&eid=CR-JE&text=" + UrlEncode(sourceNew);
            HttpWebRequest req = CreateHTTPRequest(url);
            WritePost(req, query);
            string result = ReadAnswer(req);
            Regex re = new Regex("id=\"trn_textText\".*?\\>(.*?)\\<\\/textarea\\>", RegexOptions.Singleline);
            Match m = re.Match(result);
            if (m.Success)
                return m.Groups[1].Value;
            else
                throw new Exception();
        }

        public string TranslateBabelFish()
        {
            string url = "http://babelfish.yahoo.com/translate_txt";
            //ei=UTF-8&doit=done&fr=bf-res&intl=1&tt=urltext&lp=ja_en&btnTrTxt=Translate&trtext=
            string query = "ei=UTF-8&fr=bf-badge&lp=ja_en&trtext=" + UrlEncode(sourceNew);
            HttpWebRequest req = CreateHTTPRequest(url);
            WritePost(req, query);
            string result = FindSubString(ReadAnswer(req), "<div id=\"result\"><div style=\"padding:0.6em;\">", "</div>");
            return result;
        }

        public string TranslateSysTran()
        {
            string url = "http://www.systranet.com/sai?lp=ja_en&service=translate";
            HttpWebRequest req = CreateHTTPRequest(url);
            string src_s = sourceNew.Replace('\n', ' ');
            byte[] src = Encoding.UTF8.GetBytes(src_s);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.ContentLength = src.Length;
            Stream s = req.GetRequestStream();
            s.Write(src, 0, src.Length);
            s.Close();
            string result = ReadAnswer(req);
            int x = result.IndexOf("body=");
            if (x >= 0)
            {
                result = HttpUtility.UrlDecode(result.Substring(x + 5).Trim());
            }
            return result;
        }

        public string TranslateExcite()
        {
            string url = "http://www.excite.co.jp/world/english/";
            string query = "wb_lp=JAEN&after=start=+%96%7C+%96%F3+&before=" + HttpUtility.UrlEncode(sourceNew, Encoding.GetEncoding(932));
            HttpWebRequest req = CreateHTTPRequest(url);
            WritePost(req, query);
            string result = ReadAnswer(req, Encoding.GetEncoding(932));
            Regex re = new Regex("name=\"after\".*?\\>(.*?)\\<\\/textarea\\>", RegexOptions.Singleline);
            Match m = re.Match(result);
            if (m.Success)
                return m.Groups[1].Value;
            else
                throw new Exception();
        }

        public string TranslateHivemind()
        {
            string url = new Uri(new Uri(options.hivemindServer), "query.php").ToString();
            string query = "q=" + UrlEncode(source);
            HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
            JsObject js = Json.Parse(ReadAnswer(req));
            if (js.str["success"] == "1")
            {
                return js.str["id"] + "," + js.str["result"];
            }
            else
                throw new Exception(js.str["error"]);
        }

        public string TranslateEDICT()
        {
            string[] res = MyTranslateWordsDyn(source).Split('\r');
            int pos = 0;
            List<string> ans = new List<string>();
            if (res.Length >= 5)
            {
                for (int i = 0; i < res.Length; i += 5)
                {
                    string key = res[i];
                    int x = source.IndexOf(key, pos);
                    if (x > 0)
                    {
                        string foo = source.Substring(pos, x - pos);
                        if (Global.options.furiganaRomaji)
                        {
                            foo = foo.Replace('は', 'わ');
                            foo = KatakanaToHiragana(foo);
                        }
                        ans.Add(foo);
                    }
                    if (x >= 0)
                        pos = x + key.Length;
                    bool isSuffix = false;
                    for (int j = 0; j < suffixes.GetLength(0); ++j)
                    {
                        if (suffixes[j, 0] == key)
                        {
                            ans.Add("[" + key + "]");
                            isSuffix = true;
                            break;
                        }
                    }
                    if (isSuffix)
                        continue;
                    string meaning = res[i + 4];
                    StringBuilder m2 = new StringBuilder();
                    int deep = 0;
                    for (int j = 0; j < meaning.Length; ++j)
                    {
                        if (meaning[j] == '(')
                            ++deep;
                        if (deep <= 0)
                            m2.Append(meaning[j]);
                        if (meaning[j] == ')')
                            --deep;
                    }
                    meaning = m2.ToString();
                    string[] meanings = meaning.Split(new char[] { '$' }, StringSplitOptions.RemoveEmptyEntries);
                    meanings = meanings.Take(Math.Min(meanings.Length, 3)).ToArray();
                    for (int j = 0; j < meanings.Length; ++j)
                    {
                        int p = meanings[j].IndexOf(';');
                        if (p >= 0)
                            meanings[j] = meanings[j].Substring(0, p).Trim();
                        else
                            meanings[j] = meanings[j].Trim();
                    }
                    meanings = meanings.Distinct().ToArray();
                    ans.Add("[" + string.Join("/", meanings) + "]");
                }
            }
            if (pos < source.Length)
            {
                string foo = source.Substring(pos);
                if (options.furiganaRomaji)
                {
                    foo = foo.Replace('は', 'わ');
                    foo = KatakanaToHiragana(foo);
                }
                ans.Add(foo);
            }
            string result = string.Join(" ", ans.ToArray());
            if (options.furiganaRomaji)
            {
                result = HiraganaConvertor.instance.Convert(result);
            }
            return result;
        }

        public string TranslateMyTranslit()
        {
            string[] res = MyTranslateWordsDyn(source).Split('\r');
            int pos = 0;
            List<string> ans = new List<string>();
            if (res.Length >= 5)
            {
                for (int i = 0; i < res.Length; i += 5)
                {
                    string key = res[i];
                    int x = source.IndexOf(key, pos);
                    if (x > 0)
                    {
                        string foo = source.Substring(pos, x - pos);
                        foo = foo.Replace('は', 'わ');
                        ans.Add(foo);
                    }
                    if (x >= 0)
                        pos = x + key.Length;
                    ans.Add(res[i + 1] == "" ? res[i] : res[i + 1]);
                }
            }
            if (pos < source.Length)
            {
                string foo = source.Substring(pos);
                foo = foo.Replace('は', 'わ');
                ans.Add(foo);
            }
            StringBuilder resstr = new StringBuilder();
            for (int i = 0; i < ans.Count; ++i)
            {
                string s = ans[i];
                if (i > 0 && (s.Length == 0 || char.IsLetterOrDigit(s[0])))
                    resstr.Append(' ');
                resstr.Append(s);
            }
            return HiraganaConvertor.instance.Convert(KatakanaToHiragana(resstr.ToString()));
        }

        public string TranslateAtlas()
        {
            return TranslateAtlas(sourceNew);
        }

        private string TranslateAtlas(string src)
        {
            src = Regex.Replace(src, "(?<!\\w)あ(?!\\w)", "ああ");
            src = Regex.Replace(src, "(?<!\\w)え(?!\\w)", "eh");
            src = Regex.Replace(src, "(?<!\\w)ふん(?!\\w)", "hm");
            StringBuilder res = new StringBuilder();
            StringBuilder buf = new StringBuilder();
            int i;
            for (i = 0; i < src.Length; ++i)
            {
                char ch = src[i];
                if (char.IsPunctuation(ch) && ch != '-' && ch != ',' && ch != '、' && ch != ';' && ch != '〜' && ch != ':' && ch != '：' && ch != '・' && ch != '＆')
                {
                    if (buf.Length > 0)
                    {
                        bool is_stop = (ch == '.' || ch == '?' || ch == '!' || ch == '。' || ch == '！' || ch == '？');
                        if (is_stop)
                            buf.Append(ch);
                        string tran = Atlas.Translate(buf.ToString());
                        if (tran == null)
                            throw new Exception();
                        res.Append(tran);
                        if (!is_stop)
                            res.Append(ch);
                        res.Append(' ');
                        buf = new StringBuilder();
                    }
                    else
                    {
                        res.Append(ch);
                    }
                }
                else
                    buf.Append(ch);
            }
            if (buf.Length > 0)
            {
                string tran = Atlas.Translate(buf.ToString());
                if (tran == null)
                    throw new Exception();
                res.Append(tran);
            }
            return res.ToString().Trim().Replace('。', '.').Replace('！', '!').Replace('？', '?');
        }

        public string SecondTranslate(string source, string lang)
        {
            if (lang == "ru" && Global.options.usePromt)
            {
                string url = "http://m.translate.ru/translator/result/";
                string query = "text=" + UrlEncode(source) + "&dirCode=er";
                HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
                string result = ReadAnswer(req);
                Regex re = new Regex("class=\"tres\"\\>(.*?)\\<\\/div\\>", RegexOptions.Singleline);
                Match m = re.Match(result);
                if (m.Success)
                    return m.Groups[1].Value;
                else
                    throw new Exception();
            }
            else
            {
                string url = "http://translate.google.com/translate_a/t";
                string query = "client=t&text=" + UrlEncode(source) + "&sl=en&tl=" + lang;
                HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
                string ans = ReadAnswer(req);
                JsObject js = Json.Parse(ans);
                string output = "";
                foreach (JsObject znach in js["0"])
                    output = output + " " + znach["0"].ToString();
                //string result = js["0"]["0"]["0"].ToString();
                string result = output;
                result = Regex.Replace(result, "~tsu ([A-Za-z])", match => match.Groups[1].Value + match.Groups[1].Value);
                result = result.Replace("~tsu", "");
                return result;



                
            }
        }

        private class JDicLookupSentenceRecord
        {
            public bool word;
            public string text;

            public JDicLookupSentenceRecord(bool word, string text)
            {
                this.word = word;
                this.text = text;
            }
        }
        
        private List<JDicLookupSentenceRecord> parseSentence(string s)
        {
            List<JDicLookupSentenceRecord> a = new List<JDicLookupSentenceRecord>();
            while (s.Length > 0)
            {
                if (s.Length >= 5 && s.Substring(0, 5).ToUpper() == "<FONT")
                {
                    int x = s.IndexOf(">");
                    int y = s.ToUpper().IndexOf("</FONT>");
                    string inner = s.Substring(x + 1, y - (x + 1));
                    a.Add(new JDicLookupSentenceRecord(true, inner));
                    s = s.Substring(y + 7);
                }
                else
                {
                    int x = s.ToUpper().IndexOf("<FONT");
                    string ss;
                    if (x < 0)
                    {
                        ss = s;
                        s = "";
                    }
                    else
                    {
                        ss = s.Substring(0, x);
                        s = s.Substring(x);
                    }
                    a.Add(new JDicLookupSentenceRecord(false, ss));
                }
            }
            return a;
        }

        private class JDicLookupDefinitionRecord
        {
            public string key;
            public string reading;
            public string meaning;
        }
        
        private List<JDicLookupDefinitionRecord> parseDefinitions(string text)
        {
            int i = 0;
            List<JDicLookupDefinitionRecord> res = new List<JDicLookupDefinitionRecord>();
            while (i < text.Length)
            {
                int x = text.IndexOf("<li>", i);
                if (x < 0)
                    break;
                int y = text.IndexOf("</li>", x);
                if (y < 0)
                    break;
                string s = text.Substring(x + 5, y - (x + 5));
                res.Add(parseDefinition(s));
                i = y + 4;
            }
            return res;
        }

        private JDicLookupDefinitionRecord parseDefinition(string s)
        {
            JDicLookupDefinitionRecord res = new JDicLookupDefinitionRecord();
            if (s.Length >= 8 && s.Substring(0, 8) == "Possible")
            {
                int br = s.IndexOf("<br>");
                if (br >= 0)
                {
                    s = s.Substring(br + 4);
                }
            }
            int beg = s.IndexOf(" ");
            if (beg < 0)
            {
                res.key = s;
                res.meaning = "";
                res.reading = "";
            }
            else
            {
                res.key = s.Substring(0, beg);
                s = s.Substring(beg + 1);
                beg = s.IndexOf('【');
                if (beg < 0)
                {
                    res.reading = res.key;
                    res.meaning = s;
                }
                else
                {
                    int end = s.IndexOf('】', beg);
                    res.reading = s.Substring(beg + 1, end - (beg + 1)).Trim();
                    res.meaning = s.Substring(end + 1).Trim();
                }
            }
            return res;
        }
        
        private readonly string[] JDicCodes =
            { "AV", "BU", "CA", "CC", "CO", "ED", "EP", "ES", "EV", "FM", "FO", "GE", "KD", "LG", "LS", "LW1/2", "MA",
                "NA", "PL", "PP", "RH", "RW", "SP", "ST", "WI1/2"};

        private readonly Regex htmlKiller = new Regex(@"<.+?>.*?</.+?>");

        private string JDicParse(string s1, string s2)
        {
            List<JDicLookupSentenceRecord> parts = parseSentence(s1);
            List<JDicLookupDefinitionRecord> defs = parseDefinitions(s2);

            List<string> res = new List<string>();
            int def_ctr = 0;
            foreach (JDicLookupSentenceRecord part in parts)
            {
                if (!part.word)
                    continue;
                JDicLookupDefinitionRecord cur = defs[def_ctr++];
                res.Add(part.text);
                res.Add(formatReading(part.text, cur.reading == "" ? "" : cur.reading.Split(new string[] {"; "}, StringSplitOptions.None)[0]));
                res.Add(cur.key);
                res.Add(formatReading(cur.key, cur.reading));
                List<string> mm = new List<string>();
                foreach (string m in cur.meaning.Split(new string[] { "; " }, StringSplitOptions.None))
                {
                    string newm = Edict.CleanMeaning(m);
                    newm = htmlKiller.Replace(newm, "").Trim();
                    if (newm != "" && Array.IndexOf(JDicCodes, newm) < 0)
                        mm.Add(newm);
                }
                res.Add(formatMeaning(string.Join("; ", mm)));
            }
            //Form1.Debug(string.Join("\r", res.ToArray()));
            return string.Join("\r", res.ToArray());
        }
        
        public void JDicLookup()
        {
            try
            {
                if (options.useCache)
                {
                    string cached = Global.cache.Find(source, "WWWJDIC", options.furiganaRomaji ? "r" : "h");
                    if (cached != null)
                    {
                        Global.RunScript("UpdateWords", id, cached, TranslationTask.FROM_CACHE);
                        return;
                    }
                }

                string url = options.JDicServer;
                string query = "9MIG" + HttpUtility.UrlEncode(source);
                HttpWebRequest req = CreateHTTPRequest(url + "?" + query);
                string result = ReadAnswer(req, Encoding.GetEncoding(20932));
                string beginMark = "<font size=\"-3\">&nbsp;</font><br>\n<br>\n";
                string endMark = "<br>\n<p>";
                int start = result.IndexOf(beginMark);
                if (start < 0)
                    return;
                start += beginMark.Length;
                int end = result.IndexOf(endMark, start);
                string ss1 = "";
                string ss2 = "";
                while (true)
                {
                    int fin1 = result.IndexOf("<br>", start);
                    if (fin1 > end)
                        break;
                    string s1 = result.Substring(start, fin1 - start);
                    if (s1.ToUpper().IndexOf("<FONT") < 0)
                    {
                        ss1 += s1;
                        start = fin1 + 5;
                    }
                    else
                    {
                        int x = result.IndexOf("<ul>", start);
                        if (x < 0)
                            break;
                        int y = result.IndexOf("</ul>", x + 4);
                        if (y < 0)
                            break;
                        string s2 = result.Substring(x + 4, y - (x + 4));
                        ss1 += s1;
                        ss2 += s2;
                        start = y + 5;
                    }
                }
                //Form1.Debug(ss1 + "\r\n" + ss2);
                //Form1.Debug(result);
                if (ss1 != "" && ss2 != "")
                {
                    string res = JDicParse(ss1, ss2);
                    Global.RunScript("UpdateWords", id, res, TranslationTask.COMPLETED);
                    if (options.useCache)
                    {
                        Global.cache.Store(source, "WWWJDIC", options.furiganaRomaji ? "r" : "h", res);
                    }
                }
            }
            catch (Exception) 
            {
            }
        }

        public string TranslateMecabTranslit()
        {
            string src = sourceFixed.Replace('　', ' ');
            string data = Mecab.Translate(src);
            data = data.Replace("\r", "");
            string[] ss = data.Split('\n');
            StringBuilder res = new StringBuilder();
            foreach (string s in ss)
            {
                if (s == "EOS")
                    break;
                string[] dd = s.Split(new char[] { '\t' }, 2);
                string key = dd[0];
                if (key == "")
                    key = " ";
                dd = dd[1].Split(',');
                bool haveLettersOrDigits = false;
                foreach (char ch in key)
                {
                    if (char.IsLetterOrDigit(ch))
                    {
                        haveLettersOrDigits = true;
                        break;
                    }
                }
                string chunk;
                if (dd.Length >= 9)
                    chunk = dd[8];
                else
                    chunk = key;
                chunk = KatakanaToHiragana(chunk);
                if (haveLettersOrDigits && chunk.Length > 0 && "ぁぃぅぇぉゃゅょ゜".IndexOf(chunk[0]) < 0)
                    res.Append(' ');
                res.Append(chunk);
            }
            return HiraganaToRomaji(res.ToString());
        }

        public static string formatMeaning(string meaning)
        {
            //string tmp = string.Join("; ", meaning);
            string tr = Regex.Replace(meaning, @"(?:^|; )\(\d+\)", "$");
            tr = Regex.Replace(tr, @"\#\(\d+\)", "#$");
            return tr;
        }

        private string formatReading(string key, string reading)
        {
            return formatReading(key, reading, options.furiganaRomaji);
        }

        public static string formatReading(string key, string reading, bool romaji)
        {
            bool kana = false;
            bool kanji = false;
            if (key != null)
            {
                foreach (char ch in key)
                {
                    if (char.GetUnicodeCategory(ch) == UnicodeCategory.OtherLetter)
                    {
                        kana = true;
                        if (isKanji(ch))
                        {
                            kanji = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                kana = true;
                kanji = true;
            }
            if (!kana)
                return "";
            if (romaji)
            {
                if (key == "は")
                    reading = "わ";
                /*if (!kanji)
                {
                    reading = KatakanaToHiragana(key);
                    if (reading.Length > 0 && reading[0] != 'は')
                        reading = reading.Replace('は', 'わ');
                }
                else
                {*/
                    reading = KatakanaToHiragana(reading);
                //}
                reading = HiraganaConvertor.instance.Convert(reading);
                if (reading.Length > 0 && char.IsUpper(reading[0]))
                    reading = char.ToLower(reading[0]) + reading.Substring(1);
            }
            else
            {
                if (!kanji)
                    return "";
                reading = KatakanaToHiragana(reading);
            }
            return reading;
        }

        private static bool hasKanji(string s)
        {
            foreach (char ch in s)
            {
                if (isKanji(ch))
                    return true;
            }
            return false;
        }

        class WordRecord
        {
            public string key;
            public string reading;
            public string dict_key;
            public string dict_reading;
            public string meaning;
            public int score;
        }

        private WordRecord GetWordRecordReal(string source, int st, int f2)
        {
            if (isHiragana(source[f2 - 1]) && f2 < source.Length && "ぁぃぅぇぉゃゅょ゜".IndexOf(source[f2]) >= 0)
                return null;
            string all = source.Substring(st, f2 - st);
            if (all.Length == 1 && "おかとなにねのはへやをがだでんもよぞ".IndexOf(all[0]) >= 0)
            {
                WordRecord res = new WordRecord();
                res.score = 1;
                return res;
            }
            if (all.Length <= 8)
            {
                string rep = all;
                foreach (Replacement rr in options.replacements)
                {
                    if (rr.oldText == all)
                    {
                        rep = rr.newText;
                        break;
                    }
                }
                if (rep != all)
                {
                    bool allABC = true;
                    foreach (char ch in rep)
                    {
                        var cat = char.GetUnicodeCategory(ch);
                        if (ch != '-' && cat != UnicodeCategory.SpaceSeparator && cat != UnicodeCategory.UppercaseLetter && cat != UnicodeCategory.LowercaseLetter)
                        {
                            allABC = false;
                            break;
                        }
                    }
                    if (allABC)
                    {
                        WordRecord res = new WordRecord();
                        res.key = all;
                        res.reading = rep;
                        res.dict_key = all;
                        res.dict_reading = "-";
                        res.meaning = rep;
                        res.score = 1000;
                        return res;
                    }
                }
            }
            /*if (all.Length <= 3 && all[0] == 'は')
            {
                if (st > 0 && char.IsLetter(source[st - 1]))
                    continue;
            }*/
            EdictEntry[] ex = Edict.instance.SearchExact(all, null);
            if (ex.Length > 0 && ex[0].meaning.Length > 0)
            {
                WordRecord res = new WordRecord();
                res.key = all;
                res.reading = ex[0].reading;
                res.dict_key = string.Join("#", (from ed in ex select ed.key));
                res.dict_reading = string.Join("#", (from ed in ex select ed.reading));
                res.meaning = string.Join("#", (from ed in ex select string.Join("; ", ed.meaning)));
                res.score = res.key.Length * res.key.Length;
                if (res.key != res.dict_key)
                    res.score -= 1;
                if (!Global.options.includeOkurigana)
                {
                    if (hasKanji(res.key))
                    {
                        int i = 1;
                        while (res.key.Length - i >= 0 && res.reading.Length - i >= 0 && res.key[res.key.Length - i] == res.reading[res.reading.Length - i])
                            ++i;
                        i -= 1;
                        if (i > 0)
                        {
                            res.key = res.key.Substring(0, res.key.Length - i);
                            res.reading = res.reading.Substring(0, res.reading.Length - i);
                        }
                    }
                }
                return res;
            }
            EdictEntry[] entries;
            string ending;
            string stem;
            string orig;
            bool found = Inflect.FindInflected(all, out entries, out stem, out ending, out orig);
            if (found)
            {
                var tmp = new List<WordRecord>();
                foreach (var entry in entries)
                {
                    WordRecord res = new WordRecord();
                    string key = stem + ending;
                    res.key = key;
                    int len = entry.reading.Length - orig.Length;
                    string reading;
                    if (len >= 0)
                        reading = entry.reading.Substring(0, len) + ending;
                    else
                        reading = "";
                    res.reading = reading;
                    res.dict_key = entry.key;
                    res.dict_reading = entry.reading;
                    res.meaning = string.Join("; ", entry.meaning);
                    res.score = res.key.Length * res.key.Length;
                    if (res.key != res.dict_key)
                        res.score -= 1;
                    if (!Global.options.includeOkurigana)
                    {
                        res.key = stem;
                        res.reading = entry.reading.Substring(0, len);
                    }
                    tmp.Add(res);
                }
                WordRecord allres = new WordRecord();
                allres.key = tmp[0].key;
                allres.reading = tmp[0].reading;
                allres.dict_key = string.Join("#", (from ed in tmp select ed.dict_key));
                allres.dict_reading = string.Join("#", (from ed in tmp select ed.dict_reading));
                allres.meaning = string.Join("#", (from ed in tmp select ed.meaning));
                allres.score = tmp[0].score;
                return allres;
            }
            return null;
        }
        
        private WordRecord GetWordRecord(string source, Dictionary<int, WordRecord> rec, int beg, int end, int n)
        {
            int id = beg * (n + 1) + end;
            if (rec.ContainsKey(id))
                return rec[id];
            WordRecord res = GetWordRecordReal(source, beg, end);
            rec.Add(id, res);
            return res;
        }
        
        private string MyTranslateWordsDyn(string source)
        {
            if (source == lastParsedSource)
                return lastParsedResult;
            int n = source.Length;
            int[] score = new int[n + 1];
            int[] prev = new int[n + 1];
            Dictionary<int, WordRecord> rec = new Dictionary<int,WordRecord>();
            score[0] = 0;
            for (int i = 1; i <= n; ++i)
            {
                int pr = -1;
                int maxscore = int.MinValue;
                for (int j = Math.Max(0, i - 10); j < i; ++j)
                {
                    WordRecord rr = GetWordRecord(source, rec, j, i, n);
                    int curscore;
                    if (rr != null)
                    {
                        curscore = rr.score + score[j];
                        if (curscore >= maxscore)
                        {
                            maxscore = curscore;
                            pr = j;
                        }
                    }
                    else
                    {
                        curscore = score[j] - (i - j) * (i - j) * 1000;
                        if (curscore >= maxscore)
                        {
                            maxscore = curscore;
                            pr = -1;
                        }
                    }
                }
                score[i] = maxscore;
                prev[i] = pr;
            }
            List<WordRecord> list = new List<WordRecord>();
            int x = n;
            while (x > 0)
            {
                int y = prev[x];
                if (y == -1)
                {
                    --x;
                    continue;
                }
                else
                {
                    list.Add(GetWordRecord(source, rec, y, x, n));
                    x = y;
                }
            }
            List<string> res = new List<string>();
            foreach (WordRecord rr in ((IEnumerable<WordRecord>)list).Reverse())
            {
                if (string.IsNullOrEmpty(rr.key))
                    continue;
                if (rr.dict_reading == "-")
                {
                    res.Add(rr.key);
                    res.Add(rr.reading);
                    res.Add(rr.dict_key);
                    res.Add(rr.dict_reading);
                    res.Add(rr.meaning);
                }
                else
                {
                    res.Add(rr.key);
                    res.Add(formatReading(rr.key, rr.reading));
                    res.Add(rr.dict_key);
                    res.Add(formatReading(null, rr.dict_reading));
                    string meaning;
                    if (Edict.instance.warodai != null)
                    {
                        meaning = string.Join("#", (from dk in rr.dict_key.Split('#')
                                                    where Edict.instance.warodai.ContainsKey(dk)
                                                    select Regex.Replace(Edict.instance.warodai[dk], @"\<.*?\>", "")));
                    }
                    else
                    {
                        meaning = formatMeaning(rr.meaning);
                    }
                    res.Add(meaning);
                }
            }
            lastParsedSource = source;
            lastParsedResult = string.Join("\r", res.ToArray());
            return lastParsedResult;
        }

        /*private string MyTranslateWords(string source)
        {
            if (source == lastParsedSource)
                return lastParsedResult;
            int st = 0;
            List<string> res = new List<string>();
            while (st < source.Length)
            {
                bool found = false;
                int fin = Math.Min(st + 10, source.Length);
                for (int f2 = fin - 1; f2 >= st; --f2)
                {
                    if (isHiragana(source[f2]) && f2 + 1 < source.Length && "ぁぃぅぇぉゃゅょ゜".IndexOf(source[f2 + 1]) >= 0)
                        continue;
                    string all = source.Substring(st, f2 - st + 1);
                    if (all.Length <= 8)
                    {
                        string rep = all;
                        foreach (Replacement rr in options.replacements)
                        {
                            if (rr.oldText == all)
                            {
                                rep = rr.newText;
                                break;
                            }
                        }
                        if (rep != all)
                        {
                            bool allABC = true;
                            foreach (char ch in rep)
                            {
                                var cat = char.GetUnicodeCategory(ch);
                                if (ch != '-' && cat != UnicodeCategory.SpaceSeparator && cat != UnicodeCategory.UppercaseLetter && cat != UnicodeCategory.LowercaseLetter)
                                {
                                    allABC = false;
                                    break;
                                }
                            }
                            if (allABC)
                            {
                                res.Add(all);
                                res.Add(rep);
                                res.Add(all);
                                res.Add("-");
                                res.Add(rep);
                                found = true;
                                st = f2 + 1;
                                break;
                            }
                        }
                    }
                    if (all.Length <= 3 && all[0] == 'は')
                    {
                        if (st > 0 && char.IsLetter(source[st - 1]))
                            continue;
                    }
                    EdictEntry ex = Edict.instance.SearchExact(all, null);
                    if (ex != null && ex.meaning.Length > 0)
                    {
                        res.Add(all);
                        res.Add(formatReading(all, ex.reading));
                        res.Add(ex.key);
                        res.Add(formatReading(ex.key, ex.reading));
                        res.Add(formatMeaning(ex.meaning));
                        found = true;
                        st = f2 + 1;
                        break;
                    }
                    EdictEntry entry;
                    string ending;
                    string stem;
                    string orig;
                    found = Inflect.FindInflected(all, out entry, out stem, out ending, out orig);
                    if (found)
                    {
                        string key = stem + ending;
                        res.Add(key);
                        int len = entry.reading.Length - orig.Length;
                        string reading;
                        if (len >= 0)
                            reading = entry.reading.Substring(0, len) + ending;
                        else
                            reading = "";
                        res.Add(formatReading(key, reading));
                        res.Add(entry.key);
                        res.Add(formatReading(entry.key, entry.reading));
                        res.Add(formatMeaning(entry.meaning));
                        st = f2 + 1;
                        break;
                    }
                }
                if (!found)
                {
                    st += 1;
                }
            }
            //Form1.thisForm.Text = (dbg_ctr.ToString());
            lastParsedSource = source;
            lastParsedResult = string.Join("\r", res.ToArray());
            return lastParsedResult;
        }*/

        public void BuiltinParserLookup()
        {
            try
            {
                if (options.useCache)
                {
                    string cached = Global.cache.Find(source, "Builtin", options.furiganaRomaji ? "r" : "h");
                    if (cached != null)
                    {
                        Global.RunScript("UpdateWords", id, cached, TranslationTask.FROM_CACHE);
                        return;
                    }
                }
                //long old = DateTime.Now.Ticks;
                string result = MyTranslateWordsDyn(source);
                //Form1.Debug(((double)(DateTime.Now.Ticks - old) / 10000000).ToString());
                Global.RunScript("UpdateWords", id, result, TranslationTask.COMPLETED);
                if (options.useCache)
                {
                    Global.cache.Store(source, "Builtin", options.furiganaRomaji ? "r" : "h", result);
                }
            }
            catch (Exception) 
            {
                Global.RunScript("AbortDelayed", id);
                throw;
            }
        }

        private string TranslateAtlasInline(string src)
        {
            string result;
            try
            {
                string name = "Atlas";
                if (Global.options.useCache)
                {
                    string res = Global.cache.Find(src, name, getLanguageForCache());
                    if (res != null)
                    {
                        return res;
                    }
                }
                result = TranslateAtlas(src);
                if (Global.options.translateToOtherLanguage && !Global.options.noUseSecondTranslate)
                {
                    result = SecondTranslate(result, Global.options.translateLanguage);
                }
                if (Global.options.useCache)
                {
                    Global.cache.Store(src, name, getLanguageForCache(), result);
                }
            }
            catch (Exception)
            {
                result = "";
            }
            return result;
        }

        private string getLanguageForCache()
        {
            if (options.translateToOtherLanguage)
            {
                if (options.usePromt && options.translateLanguage == "ru")
                    return "ru_promt";
                else
                    return options.translateLanguage;
            }
            else
                return "";
        }

        private string getSourceForCache(string taskName)
        {
            if (taskName.StartsWith("Translit"))
                return sourceFixed;
            else
                return sourceNew;
        }
        
        public string findCache(string taskName)
        {
            return Global.cache.Find(getSourceForCache(taskName), taskName, getLanguageForCache());
        }

        public void storeCache(string taskName, string result)
        {
            Global.cache.Store(getSourceForCache(taskName), taskName, getLanguageForCache(), result);
        }

        public static void Translate(string raw_source, Options options)
        {
            Translate(raw_source, options, false);
        }

        public static void Translate(string raw_source, Options options, bool auto)
        {
            if (options == null)
                options = Global.options;
            if (raw_source == "")
                return;
            if (raw_source.Length > options.maxSourceLength * 2)
                raw_source = raw_source.Substring(0, options.maxSourceLength * 2);
            string source = GetSource(raw_source, options);
            if (source != null && source != "" && (!auto || source != lastGoodBuffer))
            {
                if (source.Length > options.maxSourceLength)
                {
                    if (auto && options.checkRepeatingPhrasesAdv)
                        return;
                    else
                        source = source.Substring(0, options.maxSourceLength);
                }
                lastGoodBuffer = source;
                AddTranslationBlock(source, options);
            }
        }

        private static string GetSource(string raw_text, Options options)
        {
            string result = raw_text;
            if (result.ToCharArray().All(ch => !char.IsLetter(ch)))
                return null;
            if (options.checkRepeatingPhrasesAdv)
            {
                result = CheckRepeatingPhrasesAdv(result, options);
            }
            if (options.checkRepeatingPhrases)
            {
                List<string> rep;
                int n;
                rep = Djon.GetRepeatingPhrases(result, 10, out n);
                if (n > 1)
                {
                    result = string.Join("", rep.ToArray());
                }
            }
            if (options.checkDouble)
                result = CheckDouble(result);
            result = result.Trim();
            return result;
        }

        /*private static int kmp(string src, int[] d, int i, char c)
        {
            if (i == 0)
                return 0;
            if (c == src[d[i - 1]])
                return d[i - 1] + 1;
            else
                return kmp(src, d, d[i - 1], c);
        }*/

        private static bool _tryRPA(string src, string key)
        {
            int expected_at = 0;
            int bad_chars = 0;
            int bad = 0;
            int len = key.Length;
            while (true)
            {
                if (expected_at >= src.Length)
                    break;
                int is_at = src.IndexOf(key, expected_at);
                if (is_at == -1)
                {
                    bad_chars += src.Length - expected_at;
                    break;
                }
                if (is_at > expected_at)
                {
                    bad += 1;
                    bad_chars += is_at - expected_at;
                }
                expected_at = is_at + len;
            }
            return !(bad > 2 && bad_chars > src.Length / 10 || bad_chars > len * 2 || bad_chars > src.Length / 3);
        }
        
        private static string CheckRepeatingPhrasesAdv(string src, Options options)
        {
            // fuck you knuth - we use dumb force
            src = src.Trim();
            if (src.Length < 10)
                return src;
            string key = src.Substring(0, 3);
            int fst = src.IndexOf(key, 3);
            if (fst == -1)
                return src;
            int snd = src.IndexOf(key, fst + 3);
            if (snd == -1)
                return src;
            //trying first and then second
            key = src.Substring(0, fst);
            if (_tryRPA(src, key))
                return key;
            key = src.Substring(fst, snd - fst);
            if (_tryRPA(src, key))
                return key;
            return src;
        }

        public static int NextTransId()
        {
            return transId++;
        }
        
        private static void AddTranslationBlock(string source, Options options)
        {
            if (current.Count < 10)
                new Translation(NextTransId(), source, options);
        }

        private static string CheckDouble(string text)
        {
            int i = 0;
            int num = text.Length; // supposed number of repeating chars
            HashSet<int> lens = new HashSet<int>();
            while (i < text.Length)
            {
                char ch = text[i];
                if (char.IsWhiteSpace(ch))
                {
                    i += 1;
                    continue;
                }
                int j = 1;
                while (i + j < text.Length && text[i + j] == ch)
                    ++j;
                if (j == 1)
                    return text; //not double
                if (j < num)
                    num = j;
                lens.Add(j);
                i += num;
            }
            foreach (int len in lens)
            {
                if (len % num != 0)
                    return text; //not double, lol
            }
            StringBuilder res = new StringBuilder();
            i = 0;
            while (i < text.Length)
            {
                char ch = text[i];
                res.Append(ch);
                if (char.IsWhiteSpace(ch))
                {
                    i += 1;
                    int di = 1;
                    while (i < text.Length && char.IsWhiteSpace(text[i]) && di < num)
                    {
                        ++i;
                        ++di;
                    }
                }
                else
                    i += num;
            }
            return res.ToString(); //double.. or triple, or quadruple...
        }
    }
}

// こんにちは、世界！