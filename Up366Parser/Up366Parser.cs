using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Up366Parser
{
    /// <summary>
    /// 天学网听力题目解析器
    /// 支持简单题（单题）和复合题（长对话/独白）
    /// </summary>
    public class ListeningQuestionParser
    {
        /// <summary>
        /// 从文件路径解析题目编号（从 media/T{n}-ZC.mp3 提取 n）
        /// </summary>
        public static int ExtractQuestionNumber(string folderPath)
        {
            var mediaDir = Path.Combine(folderPath, "media");
            if (!Directory.Exists(mediaDir))
                return -1;

            var mp3Files = Directory.GetFiles(mediaDir, "T*-ZC.mp3");
            if (mp3Files.Length == 0)
                return -1;

            // 提取文件名中的数字，如 T2-ZC.mp3 -> 2
            var fileName = Path.GetFileNameWithoutExtension(mp3Files[0]);
            var match = Regex.Match(fileName, @"T(\d+)-ZC");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int num))
                return num;

            return -1;
        }

        /// <summary>
        /// 解析单个题目的 questionData.js 文件
        /// 自动处理简单题和复合题
        /// </summary>
        public static ListeningQuestion ParseQuestionFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"找不到文件: {filePath}");

            var jsContent = File.ReadAllText(filePath);
            return ParseQuestionContent(jsContent);
        }

        /// <summary>
        /// 从 JavaScript 内容解析题目数据
        /// 支持简单题（question_type=1）和复合题（question_type=99）
        /// </summary>
        public static ListeningQuestion ParseQuestionContent(string jsContent)
        {
            // 去掉 var pageConfig= 前缀和末尾分号，提取 JSON
            var jsonStr = Regex.Replace(jsContent.Trim(), @"^var\s+\w+\s*=\s*", "");
            jsonStr = jsonStr.TrimEnd(';', ' ');

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            var pageConfig = JsonSerializer.Deserialize<PageConfig>(jsonStr, options);

            if (pageConfig?.QuestionObj == null)
                throw new InvalidDataException("无法解析题目数据");

            return ConvertToListeningQuestion(pageConfig.QuestionObj);
        }

        /// <summary>
        /// 批量解析文件夹中的所有题目
        /// </summary>
        public static List<ListeningQuestion> ParseQuestionsFolder(string questionsFolder)
        {
            var questions = new List<ListeningQuestion>();

            if (!Directory.Exists(questionsFolder))
                return questions;

            // 获取所有题目子文件夹（乱码命名的文件夹）
            var questionDirs = Directory.GetDirectories(questionsFolder);

            foreach (var dir in questionDirs)
            {
                var dataFile = Path.Combine(dir, "questionData.js");
                if (!File.Exists(dataFile))
                    continue;

                try
                {
                    var question = ParseQuestionFile(dataFile);
                    question.FolderPath = dir;
                    question.QuestionNumber = ExtractQuestionNumber(dir);

                    // 解析音频文件路径
                    var mediaFile = Path.Combine(dir, question.MediaFile.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString()));
                    question.AudioFilePath = File.Exists(mediaFile) ? mediaFile : null;

                    questions.Add(question);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"解析失败 {dir}: {ex.Message}");
                }
            }

            // 按题号排序
            return questions.OrderBy(q => q.QuestionNumber).ToList();
        }

        private static ListeningQuestion ConvertToListeningQuestion(QuestionObjDto dto)
        {
            var question = new ListeningQuestion
            {
                QuestionId = dto.QuestionId,
                QuestionText = StripHtml(dto.QuestionText),
                Analysis = StripHtml(dto.Analysis),
                Knowledge = dto.Knowledge,
                Score = dto.QuestionScore,
                MediaFile = dto.Media?.File,
                QuestionType = dto.QuestionType,
                ElementId = dto.ElementId,
                IsComposite = dto.QuestionType == 99 || dto.QuestionsList?.Any() == true
            };

            // 判断是简单题还是复合题
            if (dto.QuestionsList?.Any() == true)
            {
                // ===== 复合题（长对话/独白）=====
                question.IsComposite = true;
                question.CompositeMainText = question.QuestionText; // "听第X段材料，回答第X至X题"

                // 根级别的 answer_text 通常是空的，答案在子题中
                question.Answer = dto.AnswerText; // 可能为空

                // 解析子题
                foreach (var subDto in dto.QuestionsList)
                {
                    var subQuestion = new SubQuestion
                    {
                        QuestionId = subDto.QuestionId,
                        QuestionText = StripHtml(subDto.QuestionText),
                        Answer = subDto.AnswerText,
                        Analysis = StripHtml(subDto.Analysis),
                        Knowledge = subDto.Knowledge,
                        Score = subDto.QuestionScore,
                        ElementId = subDto.ElementId
                    };

                    // 解析子题选项
                    if (subDto.Options != null)
                    {
                        foreach (var opt in subDto.Options)
                        {
                            subQuestion.Options.Add(new QuestionOption
                            {
                                Id = opt.Id,
                                Content = opt.Content,
                                IsCorrect = opt.Id.Equals(subDto.AnswerText, StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }

                    question.SubQuestions.Add(subQuestion);
                }

                // 复合题的主文本通常是引导语，如"听第7段材料，回答第8至10题"
                // 如果主文本包含这种引导语，尝试提取题号范围
                ExtractQuestionRange(question);
            }
            else
            {
                // ===== 简单题（单题）=====
                question.IsComposite = false;
                question.Answer = dto.AnswerText;

                // 解析选项
                if (dto.Options != null)
                {
                    foreach (var opt in dto.Options)
                    {
                        question.Options.Add(new QuestionOption
                        {
                            Id = opt.Id,
                            Content = opt.Content,
                            IsCorrect = opt.Id.Equals(dto.AnswerText, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }

            // 解析原文对话（简单题和复合题都有）
            if (dto.RecordFollowRead?.ParagraphList != null)
            {
                foreach (var para in dto.RecordFollowRead.ParagraphList)
                {
                    var paragraph = new DialogueParagraph
                    {
                        Speaker = para.Pre
                    };

                    if (para.Sentences != null)
                    {
                        foreach (var sent in para.Sentences)
                        {
                            paragraph.Sentences.Add(new DialogueSentence
                            {
                                Id = sent.Id,
                                EnglishText = sent.ContentEn,
                                ChineseText = sent.ContentCn,
                                NetFiles = sent.NetFiles?.ToList() ?? new List<string>(),
                                StartTime = sent.StartTime,
                                EndTime = sent.EndTime,
                                KeyNo = sent.KeyNo // 标记关键句（对应题目）
                            });
                        }
                    }

                    question.DialogueParagraphs.Add(paragraph);
                }
            }

            // 构建完整原文文本
            question.FullTranscript = BuildTranscript(question.DialogueParagraphs);

            return question;
        }

        /// <summary>
        /// 从复合题引导语中提取题号范围
        /// 如 "听第7段材料，回答第8至10题" -> StartSubNumber=8, EndSubNumber=10
        /// </summary>
        private static void ExtractQuestionRange(ListeningQuestion question)
        {
            if (string.IsNullOrEmpty(question.CompositeMainText))
                return;

            // 匹配 "回答第8至10题" 或 "回答第14至16题"
            var match = Regex.Match(question.CompositeMainText, @"回答第(\d+)至(\d+)题");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int start))
                    question.SubQuestionStartNumber = start;
                if (int.TryParse(match.Groups[2].Value, out int end))
                    question.SubQuestionEndNumber = end;
            }

            // 匹配 "回答第6、7题"
            var match2 = Regex.Match(question.CompositeMainText, @"回答第(\d+)、(\d+)题");
            if (match2.Success)
            {
                if (int.TryParse(match2.Groups[1].Value, out int start))
                    question.SubQuestionStartNumber = start;
                if (int.TryParse(match2.Groups[2].Value, out int end))
                    question.SubQuestionEndNumber = end;
            }
        }

        /// <summary>
        /// 去除HTML标签并解码HTML实体
        /// </summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;

            // 移除 HTML 标签
            var text = Regex.Replace(html, @"<[^>]+>", "");
            // 解码 HTML 实体
            text = WebUtility.HtmlDecode(text);
            return text.Trim();
        }

        /// <summary>
        /// 格式化解析文本，使其与原文对应
        /// 将连续文本转换为对话格式
        /// </summary>
        public static string FormatAnalysis(string analysis, List<DialogueParagraph> paragraphs)
        {
            if (string.IsNullOrWhiteSpace(analysis))
                return analysis;

            // 如果解析已经是格式化的对话形式，直接返回
            if (analysis.Contains("M:") || analysis.Contains("W:"))
                return analysis;

            // 否则，尝试根据段落信息重新格式化
            var formattedLines = new List<string>();

            foreach (var para in paragraphs)
            {
                var speaker = para.Speaker switch
                {
                    "M" => "M",
                    "W" => "W",
                    _ => para.Speaker
                };

                foreach (var sent in para.Sentences)
                {
                    var line = $"{speaker}: {sent.EnglishText}";
                    if (!string.IsNullOrEmpty(sent.KeyNo))
                    {
                        line += $" ({sent.KeyNo})";
                    }
                    formattedLines.Add(line);
                }
            }

            return string.Join("\n", formattedLines);
        }

        private static string BuildTranscript(List<DialogueParagraph> paragraphs)
        {
            var lines = new List<string>();
            foreach (var para in paragraphs)
            {
                // 保持简洁：M=男, W=女, N=旁白/独白
                var speaker = para.Speaker switch
                {
                    "M" => "M",
                    "W" => "W",
                    "" => "N",
                    _ => para.Speaker
                };

                foreach (var sent in para.Sentences)
                {
                    var keyMarker = "";
                    if (!string.IsNullOrEmpty(sent.KeyNo))
                    {
                        // KeyNo 可能是 "8,9" 这种逗号分隔
                        var nos = sent.KeyNo.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(n => n.Trim())
                                           .Where(n => !string.IsNullOrEmpty(n));
                        keyMarker = string.Join("", nos.Select(n => $"({n})"));
                    }
                    lines.Add($"{speaker}: {sent.EnglishText}{keyMarker}");
                }
            }
            return string.Join("\n", lines);
        }
    }

    #region 数据模型

    /// <summary>
    /// 听力题目完整信息（支持简单题和复合题）
    /// </summary>
    public class ListeningQuestion
    {
        /// <summary>题目唯一ID（MD5哈希）</summary>
        public string QuestionId { get; set; }

        /// <summary>题号（从音频文件名提取，如T2-ZC.mp3 -> 2）</summary>
        public int QuestionNumber { get; set; }

        /// <summary>是否为复合题（长对话/独白含多个子题）</summary>
        public bool IsComposite { get; set; }

        /// <summary>题目主文本（简单题=问题，复合题=引导语）</summary>
        public string QuestionText { get; set; }

        /// <summary>复合题引导语（如"听第7段材料，回答第8至10题"）</summary>
        public string CompositeMainText { get; set; }

        /// <summary>子题起始编号（复合题用，如8）</summary>
        public int? SubQuestionStartNumber { get; set; }

        /// <summary>子题结束编号（复合题用，如10）</summary>
        public int? SubQuestionEndNumber { get; set; }

        /// <summary>简单题的选项列表</summary>
        public List<QuestionOption> Options { get; set; } = new();

        /// <summary>简单题的答案（A/B/C/D）</summary>
        public string Answer { get; set; }

        /// <summary>子题列表（复合题用）</summary>
        public List<SubQuestion> SubQuestions { get; set; } = new();

        /// <summary>解析（已去除HTML）</summary>
        public string Analysis { get; set; }

        /// <summary>知识点标签</summary>
        public string Knowledge { get; set; }

        /// <summary>总分值（复合题为各子题分值之和）</summary>
        public double Score { get; set; }

        /// <summary>音频文件相对路径</summary>
        public string MediaFile { get; set; }

        /// <summary>音频文件绝对路径（如果存在）</summary>
        public string AudioFilePath { get; set; }

        /// <summary>题目类型（1=简单题，99=复合题）</summary>
        public int QuestionType { get; set; }

        /// <summary>元素ID</summary>
        public string ElementId { get; set; }

        /// <summary>原文对话段落</summary>
        public List<DialogueParagraph> DialogueParagraphs { get; set; } = new();

        /// <summary>完整原文文本</summary>
        public string FullTranscript { get; set; }

        /// <summary>源文件夹路径</summary>
        public string FolderPath { get; set; }

        /// <summary>获取显示用题号（复合题显示范围如"8-10"）</summary>
        public string GetDisplayNumber()
        {
            if (IsComposite && SubQuestionStartNumber.HasValue && SubQuestionEndNumber.HasValue)
            {
                if (SubQuestionStartNumber == SubQuestionEndNumber)
                    return SubQuestionStartNumber.ToString();
                return $"{SubQuestionStartNumber}-{SubQuestionEndNumber}";
            }
            return QuestionNumber.ToString();
        }

        public override string ToString()
        {
            if (IsComposite)
                return $"[T{QuestionNumber} 复合题 {GetDisplayNumber()}] {CompositeMainText}";
            return $"[T{QuestionNumber}] {QuestionText}";
        }
    }

    /// <summary>
    /// 子题（用于复合题）
    /// </summary>
    public class SubQuestion
    {
        /// <summary>子题ID</summary>
        public string QuestionId { get; set; }

        /// <summary>子题文本</summary>
        public string QuestionText { get; set; }

        /// <summary>子题答案</summary>
        public string Answer { get; set; }

        /// <summary>子题选项</summary>
        public List<QuestionOption> Options { get; set; } = new();

        /// <summary>子题解析（已美化格式）</summary>
        public string Analysis { get; set; }

        /// <summary>子题知识点</summary>
        public string Knowledge { get; set; }

        /// <summary>子题分值</summary>
        public double Score { get; set; }

        /// <summary>元素ID</summary>
        public string ElementId { get; set; }

        public override string ToString()
        {
            return $"[{Answer}] {QuestionText}";
        }
    }

    /// <summary>
    /// 题目选项
    /// </summary>
    public class QuestionOption
    {
        /// <summary>选项ID（A/B/C/D）</summary>
        public string Id { get; set; }

        /// <summary>选项内容</summary>
        public string Content { get; set; }

        /// <summary>是否为正确答案</summary>
        public bool IsCorrect { get; set; }

        public override string ToString()
        {
            var marker = IsCorrect ? " ✓" : "";
            return $"{Id}. {Content}{marker}";
        }
    }

    /// <summary>
    /// 原文对话段落（按说话人分组）
    /// </summary>
    public class DialogueParagraph
    {
        /// <summary>说话人（M=男，W=女，空=独白）</summary>
        public string Speaker { get; set; }

        /// <summary>句子列表</summary>
        public List<DialogueSentence> Sentences { get; set; } = new();
    }

    /// <summary>
    /// 原文句子
    /// </summary>
    public class DialogueSentence
    {
        /// <summary>句子ID</summary>
        public int Id { get; set; }

        /// <summary>英文原文</summary>
        public string EnglishText { get; set; }

        /// <summary>中文翻译（通常为空）</summary>
        public string ChineseText { get; set; }

        /// <summary>关联的net文件（口型动画数据）</summary>
        public List<string> NetFiles { get; set; } = new();

        /// <summary>开始时间</summary>
        public string StartTime { get; set; }

        /// <summary>结束时间</summary>
        public string EndTime { get; set; }

        /// <summary>关键句标记（对应题号，如"8,9"表示对应第8、9题）</summary>
        public string KeyNo { get; set; }
    }

    #endregion

    #region JSON DTOs

    internal class PageConfig
    {
        [JsonPropertyName("questionObj")]
        public QuestionObjDto QuestionObj { get; set; }
    }

    internal class QuestionObjDto
    {
        [JsonPropertyName("question_id")]
        public string QuestionId { get; set; }

        [JsonPropertyName("question_text")]
        public string QuestionText { get; set; }

        [JsonPropertyName("answer_text")]
        public string AnswerText { get; set; }

        [JsonPropertyName("analysis")]
        public string Analysis { get; set; }

        [JsonPropertyName("knowledge")]
        public string Knowledge { get; set; }

        [JsonPropertyName("question_score")]
        public double QuestionScore { get; set; }

        [JsonPropertyName("question_type")]
        public int QuestionType { get; set; }

        [JsonPropertyName("element_id")]
        public string ElementId { get; set; }

        [JsonPropertyName("media")]
        public MediaDto Media { get; set; }

        [JsonPropertyName("options")]
        public List<OptionDto> Options { get; set; }

        [JsonPropertyName("record_follow_read")]
        public RecordFollowReadDto RecordFollowRead { get; set; }

        // 复合题特有：子题列表
        [JsonPropertyName("questions_list")]
        public List<SubQuestionDto> QuestionsList { get; set; }
    }

    internal class SubQuestionDto
    {
        [JsonPropertyName("question_id")]
        public string QuestionId { get; set; }

        [JsonPropertyName("question_text")]
        public string QuestionText { get; set; }

        [JsonPropertyName("answer_text")]
        public string AnswerText { get; set; }

        [JsonPropertyName("analysis")]
        public string Analysis { get; set; }

        [JsonPropertyName("knowledge")]
        public string Knowledge { get; set; }

        [JsonPropertyName("question_score")]
        public double QuestionScore { get; set; }

        [JsonPropertyName("element_id")]
        public string ElementId { get; set; }

        [JsonPropertyName("options")]
        public List<OptionDto> Options { get; set; }
    }

    internal class MediaDto
    {
        [JsonPropertyName("file")]
        public string File { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }
    }

    internal class OptionDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }

        [JsonPropertyName("flag")]
        public int Flag { get; set; }
    }

    internal class RecordFollowReadDto
    {
        [JsonPropertyName("paragraph_list")]
        public List<ParagraphDto> ParagraphList { get; set; }

        [JsonPropertyName("mode_list")]
        public List<ModeDto> ModeList { get; set; }
    }

    internal class ParagraphDto
    {
        [JsonPropertyName("pre")]
        public string Pre { get; set; }

        [JsonPropertyName("sentences")]
        public List<SentenceDto> Sentences { get; set; }
    }

    internal class SentenceDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("content_en")]
        public string ContentEn { get; set; }

        [JsonPropertyName("content_cn")]
        public string ContentCn { get; set; }

        [JsonPropertyName("net_files")]
        public List<string> NetFiles { get; set; }

        [JsonPropertyName("startTime")]
        public string StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public string EndTime { get; set; }

        [JsonPropertyName("keyNo")]
        public string KeyNo { get; set; }
    }

    internal class ModeDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("media_file")]
        public string MediaFile { get; set; }

        [JsonPropertyName("sentences")]
        public List<ModeSentenceDto> Sentences { get; set; }
    }

    internal class ModeSentenceDto
    {
        [JsonPropertyName("ref")]
        public int Ref { get; set; }

        [JsonPropertyName("startTime")]
        public string StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public string EndTime { get; set; }
    }

    #endregion
}