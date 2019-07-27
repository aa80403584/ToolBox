﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace ToolBox.Socket
{
    public partial class TcpServer
    {

        #region 变量

        /// <summary>
        /// 负责监听连接的线程
        /// </summary>
        private Thread threadWatch = null;

        /// <summary>
        /// 服务端监听套接字
        /// </summary>
        private System.Net.Sockets.Socket socketWatch = null;   

        /// <summary>
        /// 客户端的字典
        /// </summary>
        private Dictionary<string, ClientMode> dictsocket = new Dictionary<string, ClientMode>();

        /// <summary>
        /// 读写线程锁
        /// </summary>
        private ReaderWriterLockSlim lockSlim = new ReaderWriterLockSlim();

        /// <summary>
        /// 心跳时间（默认超过7秒没收到心跳事件就把客户端清除）
        /// </summary>
        public long HearTime { get; set; } = 7;         

        /// <summary>
        /// 心跳检查间隔（Heartbeat check interval）
        /// </summary>
        public int HeartbeatCheckInterval { get; set; } = 3000;


        #endregion

        /// <summary>
        /// 开始服务器
        /// </summary>
        /// <param name="port">端口号</param>
        /// <param name="count">连接队列总数（默认50）</param>
        /// <param name="ip">ip地址（默认本机ip）</param>
        public void StartServer(int port, int count = 50, string ip = "127.0.0.1")
        {
            socketWatch = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            IPAddress ipAdr = IPAddress.Parse(ip);
            IPEndPoint iPEnd = new IPEndPoint(ipAdr, port);

            try
            {
                socketWatch.Bind(iPEnd);
            }
            catch (Exception ex)
            {
                writeMsg("启动服务时的异常：" + ex.ToString());
                return;
            }

            socketWatch.Listen(count);

            //开启心脏检测
            Task.Run(() =>
            {
                while (true)
                {
                    HearBeat();
                    Thread.Sleep(HeartbeatCheckInterval);
                }

            });

           // 监听客户端请求的方法，线程
            Task.Run(() =>
            {

                while (true)
                {
                  
                    System.Net.Sockets.Socket conn = socketWatch.Accept();
                    string socketip = conn.RemoteEndPoint.ToString();

                    OnClientAdd?.Invoke("进来新的客户端ip:" + socketip);
                    conn.Send(SocketTools.GetBytes("YouIP," + socketip));

                    Thread thr = new Thread(RecMsg);
                    thr.IsBackground = true;
                    thr.Start(conn);

                    AddSocketClient(socketip, conn, thr);

                }

            });

            OnSuccess?.Invoke("服务器启动监听成功~");

        }


        /// <summary>
        /// 写入输出信息
        /// </summary>
        /// <param name="msg"></param>
        private void writeMsg(string msg)
        {
            OnMessage?.Invoke(msg);
        }


        /// <summary>
        /// 接收信息
        /// </summary>
        /// <param name="socket"></param>
        private void RecMsg(object socket)
        {
            int headSize = 4;
            byte[] surplusBuffer = null;
            System.Net.Sockets.Socket  sokClient = socket as System.Net.Sockets.Socket;
            string socketip = sokClient.RemoteEndPoint.ToString();

            while (true)
            {
                int count = -1;
                try
                {
                    byte[] vs = new byte[1024];
                    count = sokClient.Receive(vs); // 接收数据，并返回数据的长度；
                    int bytesRead = vs.Length;

                    if (bytesRead > 0)
                    {

                        if (surplusBuffer == null)
                        {
                            surplusBuffer = vs;
                        }
                        else
                        {
                            surplusBuffer = surplusBuffer.Concat(vs).ToArray();
                        }

                        int haveRead = 0;
                        int totalLen = surplusBuffer.Length;

                        while (haveRead <= totalLen)
                        {
                            if (totalLen - haveRead < headSize)
                            {
                                //Console.WriteLine("不够一个包~");
                                byte[] byteSub = new byte[totalLen - haveRead];
                                Buffer.BlockCopy(surplusBuffer, haveRead, byteSub, 0, totalLen - haveRead);
                                surplusBuffer = byteSub;
                                totalLen = 0;
                                break;
                            }

                            //如果是够一个完整包了，帽读取包头的数据
                            byte[] headByte = new byte[headSize];
                            Buffer.BlockCopy(surplusBuffer, haveRead, headByte, 0, headSize);

                            int bodySize = BitConverter.ToInt32(headByte, 0);   //得到长度

                            if (bodySize == 0)
                            {
                                surplusBuffer = null;
                                totalLen = 0;
                                break;
                            }

                            //这里的 haveRead=等于N个数据包的长度 从0开始；0,1,2,3....N
                            //如果自定义缓冲区拆解N个包后的长度 大于 总长度，说最后一段数据不够一个完整的包了，拆出来保存
                            if (haveRead + headSize + bodySize > totalLen)
                            {
                                byte[] byteSub = new byte[totalLen - haveRead];
                                Buffer.BlockCopy(surplusBuffer, haveRead, byteSub, 0, totalLen - haveRead);
                                surplusBuffer = byteSub;
                                // Console.WriteLine("不够一个包，拆出来保存");
                                break;
                            }
                            else
                            {
                                string strc = Encoding.UTF8.GetString(surplusBuffer, haveRead + headSize, bodySize);


                                
                                string[] ss = strc.Split(',');

                                //心跳事件，更新客户端的最后登陆时间
                                if (ss.Count() == 2 && ss[0].ToString().Equals("hear"))
                                {

                                    // 心跳事件 0=hert,1=ip
                                    lockSlim.EnterWriteLock();

                                    try
                                    {

                                        ClientMode socketClient;
                                        if (dictsocket.TryGetValue(ss[1].ToString(), out socketClient))
                                        {

                                           // writeMsg("更新时间便签：" + SocketTools.GetTimeStamp() + ss[1].ToString());
                                            socketClient.lastTickTime = SocketTools.GetTimeStamp();

                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        OnError?.Invoke("心跳事件报错：:" + ex.ToString());
                                    
                                    }
                                    finally
                                    {
                                        lockSlim.ExitWriteLock();
                                    }

                                }
                                else
                                {
                                    OnRecMessage?.Invoke(socketip, strc);
                                }

                                haveRead = haveRead + headSize + bodySize;
                                if (headSize + bodySize == bytesRead)
                                {
                                    surplusBuffer = null;
                                    totalLen = 0;
                                }

                            }

                        }

                    }

                }
                catch (Exception ex)
                {
                    ReMoveSocketClient(socketip);
                
                    OnError?.Invoke("接收客户端的线程报错: " + socketip);
                    break;
                }


            }

        }



    }


    
}
