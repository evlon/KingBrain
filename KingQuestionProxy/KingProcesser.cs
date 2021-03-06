﻿using Fiddler;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NetworkSocket;
using NetworkSocket.Http;
using NetworkSocket.WebSocket;
using Newtonsoft.Json;
using System.Net;
using System.Configuration;
using System.Diagnostics;

namespace KingQuestionProxy
{
    /// <summary>
    /// 王者数据处理器
    /// </summary>
    static class KingProcesser
    {
        /// <summary>
        /// http和ws监听器
        /// </summary>
        private static readonly TcpListener listener = new TcpListener();

        /// <summary>
        /// 王者数据处理器
        /// </summary>
        static KingProcesser()
        {
            var http = listener.Use<HttpMiddleware>();
            http.MIMECollection.Add(".cer", "application/x-x509-ca-cert");

            listener.Use<WebsocketMiddleware>();
            listener.Start(AppConfig.WsPort);
        }

        /// <summary>
        /// 显式初始化
        /// </summary>
        public static void Init()
        {
        }

        /// <summary>
        /// 关闭监听器
        /// </summary>
        public static void CloseListener()
        {
            listener.Dispose();
        }

        /// <summary>
        /// 处理会话
        /// </summary>
        /// <param name="session">会话</param>
        /// <returns></returns>

        public static void ProcessSession(Session session)
        {
            var url = session.fullUrl;
            if (url.Contains("question/bat/findQuiz") == true)
            {
                SetResponseWithAnswer(session);
            }
            else if (url.Contains("question/bat/choose") == true)
            {
                UpdateCorrectOptions(session);
            }
            else if (url.Contains("question/bat/fightResult") == true)
            {
                var notifyData = new WsNotifyData<object> { Cmd = WsCmd.GameOver };
                WsNotifyByClientIP(notifyData, session.clientIP);
            }
        }

        /// <summary>
        /// 从本地和网络查找答案
        /// 并转发给对应的ws客户端
        /// </summary>
        /// <param name="session">会话</param>
        private static void SetResponseWithAnswer(Session session)
        {
            var beginTime = DateTime.Now;
            var optionIndex = GetOptionIndex(session, out KingQuestion kingQuestion);
            if (kingQuestion == null || kingQuestion.IsValidate() == false)
            {
                return;
            }

            // 推送答案给ws客户端
            const double offsetSecondes = 3.7d;
            var delay = (int)beginTime.AddSeconds(offsetSecondes).Subtract(DateTime.Now).TotalMilliseconds;
            var gameAnswer = new WsGameAnswer
            {
                Index = optionIndex,
                Quiz = kingQuestion.data.quiz,
                Options = kingQuestion.data.options,
                DelayMilliseconds = delay
            };
            var notifyData = new WsNotifyData<WsGameAnswer>
            {
                Cmd = WsCmd.GameAnser,
                Data = gameAnswer,
            };
            WsNotifyByClientIP(notifyData, session.clientIP);


            // 改写响应结果
            if (AppConfig.ResponseAnswer == true && optionIndex > -1)
            {
                var json = JsonConvert.SerializeObject(kingQuestion);
                kingQuestion = JsonConvert.DeserializeObject<KingQuestion>(json);
                var quizData = kingQuestion.data;

                quizData.quiz = quizData.quiz + $" 老九提示：[{(char)('A' + optionIndex)}]";
                quizData.options[optionIndex] = quizData.options[optionIndex] + " [√]";

                json = JsonConvert.SerializeObject(kingQuestion);
                session.utilSetResponseBody(json);
            }
        }

        /// <summary>
        /// 从本地和网络查找答案
        /// 返回正确选项的索引
        /// </summary>
        /// <param name="session">会话</param>
        /// <returns></returns>
        private static int GetOptionIndex(Session session, out KingQuestion kingQuestion)
        {
            kingQuestion = KingQuestion.FromSession(session);
            if (kingQuestion == null || kingQuestion.IsValidate() == false)
            {
                return -1;
            }

            KingContextTable.Add(new KingContext
            {
                KingQuestion = kingQuestion,
                KingRequest = KingRequest.FromSession(session)
            });

            // 找答案
            return SearchOptionIndex(kingQuestion);
        }

        /// <summary>
        /// 查找问题答案并保存到db
        /// </summary>
        /// <param name="kingQuestion">问题</param>
        /// <returns></returns>
        private static int SearchOptionIndex(KingQuestion kingQuestion)
        {
            using (var sqlLite = new SqlliteContext())
            {
                var quiz = kingQuestion.data.quiz;
                var quizAnswer = sqlLite.QuizAnswer.FirstOrDefault(item => item.Quiz == quiz);

                if (quizAnswer != null)
                {
                    Console.WriteLine($"从db中找到记录：{Environment.NewLine}{quizAnswer}");
                    var answer = quizAnswer.Answer?.Trim();
                    return Array.FindIndex(kingQuestion.data.options, item => item?.Trim() == answer);
                }

                // 搜索
                var best = Searcher.Search(kingQuestion);
                if (best == null)
                {
                    Console.WriteLine($"找不到答案：{kingQuestion.data.quiz}");
                    return -1;
                }

                if (sqlLite.QuizAnswer.Any(item => item.Quiz == quiz) == false)
                {
                    quizAnswer = new QuizAnswer
                    {
                        Answer = best.Option,
                        Quiz = quiz,
                        OptionsJson = JsonConvert.SerializeObject(kingQuestion.data.options)
                    };
                    sqlLite.QuizAnswer.Add(quizAnswer);
                    sqlLite.SaveChanges();
                    Console.WriteLine($"保存网络答案到db：{Environment.NewLine}{quizAnswer}");
                }
                return best.Index;
            }
        }

        /// <summary>
        /// 发送答案给ws客户端
        /// </summary>
        /// <param name="notifyData">数据内容</param>
        /// <param name="clientIp">客户端ip</param>
        private static void WsNotifyByClientIP(IWsNotifyData notifyData, string clientIp)
        {
            var jsonResult = notifyData.ToJson();
            var wsSessions = listener.SessionManager.FilterWrappers<WebSocketSession>();

            foreach (var ws in wsSessions)
            {
                try
                {
                    var ip = ws.Tag.Get("ip").ToString();
                    if (clientIp == ip || clientIp.Contains(ip))
                    {
                        ws.SendText(jsonResult);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 更新最佳选项到db
        /// </summary>
        /// <param name="session"></param>
        private static void UpdateCorrectOptions(Session session)
        {
            var kingRequest = KingRequest.FromSession(session);
            var kingAnswer = KingAnswer.FromSession(session);

            if (kingAnswer == null || kingAnswer.IsValidate() == false)
            {
                return;
            }

            var context = KingContextTable.TakeByRequest(kingRequest);
            if (context == null)
            {
                return;
            }

            using (var sqlLite = new SqlliteContext())
            {
                var quiz = context.KingQuestion.data.quiz;
                var quizAnswer = sqlLite.QuizAnswer.Find(quiz);

                if (quizAnswer != null)
                {
                    quizAnswer.Answer = context.GetAnswer(kingAnswer);
                    sqlLite.SaveChanges();
                    Console.WriteLine($"更新正确答案到db：{Environment.NewLine}{quizAnswer}");
                }
            }
        }
    }
}
