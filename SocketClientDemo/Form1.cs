using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Net;
using MaterialSkin.Controls;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Collections;
using System.IO;

namespace SocketClientDemo
{
    public partial class Form1 : MaterialForm
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, int minSize, int maxSize);

        #region 已知信息存有以下信息的DataTable表

        private DataTable dt_ServerInfo = new DataTable();

        #endregion

        #region 变量声明

        private ConcurrentDictionary<string, Socket> socketClients = new ConcurrentDictionary<string, Socket>();    //客户端套接字集合

        private ArrayList clientsockets = new ArrayList();

        private byte[] ReceiveByte_HFCT = new byte[24];
        private byte[] ReceiveByte_CIRC = new byte[64];

        private volatile bool Stopflag = false;  //收发标志  --  加入 volatile 修饰符保证不被优化掉

        IPEndPoint serverEndPoint = null;

        #endregion

        #region 发送线程，接收线程,心跳检测线程

        Thread Thr_Send = null;
        Thread Thr_Receive = null;
        Thread Thr_KeepAllive = null;

        #endregion

        public Form1()
        {
            InitializeComponent();
            GenerateDataTable();
            CreateSocketConnection();
            StartReceiveData();
            StartCheckAlive();
        }

        #region 自定义函数
        /// <summary>
        /// 与客户端建立连接：若出错，则开辟一个新线程，在新线程里每隔五秒尝试连接一次，连接成功的话跳出循环，加入到列表中
        /// </summary>
        private void CreateSocketConnection()
        {
            int countOfServers = dt_ServerInfo.Rows.Count;
            Socket serverSocket = null;

            for (int i = 0; i < countOfServers; i++)
            {
                try
                {
                    serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    serverEndPoint = new IPEndPoint(IPAddress.Parse(dt_ServerInfo.Rows[i]["serverIP"].ToString()),
                                                                   int.Parse(dt_ServerInfo.Rows[i]["serverPort"].ToString()));
                    serverSocket.BeginConnect(serverEndPoint, new AsyncCallback(ConnectCallBack), serverSocket);
                }
                catch (Exception)
                {
                    continue;
                }
            }

        }

        private void ConnectCallBack(IAsyncResult asyncResult)
        {
            Socket tempSocket = (Socket)asyncResult.AsyncState;
            try
            {
                tempSocket.EndConnect(asyncResult);
                socketClients.TryAdd(tempSocket.RemoteEndPoint.ToString(), tempSocket);
            }
            catch (Exception ex)
            {
                WriteLog(ex);
                Thread thr_connect = new Thread(() =>
                {
                    bool isConnected = false;
                    IPEndPoint temppoint = serverEndPoint;
                    do
                    {
                        tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            tempSocket.Connect(temppoint);
                            isConnected = true;
                            socketClients.TryAdd(tempSocket.RemoteEndPoint.ToString(), tempSocket);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    } while (!isConnected);
                });
                thr_connect.IsBackground = true;
                thr_connect.Start();
            }
        }

        /// <summary>
        /// Socket 重连
        /// </summary>
        /// <param name="ErrorClient">出现问题的Socket</param>
        /// <returns>重连状态</returns>
        private bool IsConnected(Socket ErrorClient)
        {
            bool _isConnected = false;
            ErrorClient.Close();
            IPEndPoint temppoint = (IPEndPoint)ErrorClient.RemoteEndPoint;
            try
            {
                IAsyncResult result = ErrorClient.BeginConnect(temppoint, null, null);
                _isConnected = result.AsyncWaitHandle.WaitOne(500);
                return _isConnected;
            }
            catch (SocketException)
            { throw; }
        }

        private void SendThread()
        {
            while (true)
            {
                if (!Stopflag)
                {
                    foreach (var item in socketClients)
                    {
                        string serverInfo = item.Key.ToString();
                        DataRow[] drs = dt_ServerInfo.Select($"ServerIp = '{serverInfo.Split(':')[0]}' And ServerPort = '{serverInfo.Split(':')[1]}'");
                        byte[] sendByte = hexStringToByteArray(drs[0]["Command"].ToString());
                        try
                        {
                            item.Value.BeginSend(sendByte, 0, sendByte.Length, SocketFlags.None, new AsyncCallback(SendCallback), item.Value);
                        }
                        catch (SocketException)
                        {
                            Thread thr_ReConnecting = new Thread(() =>
                            {
                                Socket client = item.Value;
                                if (client != null && !client.Connected)
                                {
                                    IPEndPoint temppoint = (IPEndPoint)client.RemoteEndPoint;
                                    socketClients.TryRemove(client.RemoteEndPoint.ToString(), out client);
                                    client.Shutdown(SocketShutdown.Both);
                                    client.Close();
                                    bool isConnected = false;
                                    do
                                    {
                                        try
                                        {
                                            Socket tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                                            tempSocket.Connect(temppoint);
                                            isConnected = true;
                                            socketClients.TryAdd(tempSocket.RemoteEndPoint.ToString(), tempSocket);
                                        }
                                        catch (Exception)
                                        {
                                            continue;
                                        }

                                    } while (!isConnected);
                                }
                            });
                            thr_ReConnecting.IsBackground = true;
                            thr_ReConnecting.Start();
                            continue;
                        }
                    }
                    DateTime now = DateTime.Now;
                    while (now.AddSeconds(1) > DateTime.Now) { }
                }
            }
        }

        private void SendCallback(IAsyncResult asyncResult)
        {
            Socket client = (Socket)asyncResult.AsyncState; //客户端套接字
            try
            {
                client.EndSend(asyncResult);
            }
            catch (Exception ex)
            {
                WriteLog(ex);
                Thread thr_ReConnecting = new Thread(() =>
                {
                    if (client != null && client.Connected)
                    {
                        IPEndPoint temppoint = (IPEndPoint)client.RemoteEndPoint;
                        socketClients.TryRemove(client.RemoteEndPoint.ToString(), out client);
                        client.Shutdown(SocketShutdown.Both);
                        bool isConnected = false;
                        do
                        {
                            try
                            {
                                Socket tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                                tempSocket.Connect(temppoint);
                                isConnected = true;
                                socketClients.TryAdd(tempSocket.RemoteEndPoint.ToString(), tempSocket);
                            }
                            catch (Exception)
                            {
                                continue;
                            }

                        } while (!isConnected);
                    }
                });
                thr_ReConnecting.IsBackground = true;
                thr_ReConnecting.Start();
            }
        }

        private void AsyncReceiveCall(IAsyncResult asyncResult)
        {
            Socket client = (Socket)asyncResult.AsyncState;
            try
            {
                int bytesRead = client.EndReceive(asyncResult);
                byte[] buffer = new byte[bytesRead];
                string DataRecv = string.Empty;

                if (bytesRead == 64)
                {
                    DataRecv = byteArrayToHexString(ReceiveByte_CIRC);
                }
                else
                {
                    DataRecv = byteArrayToHexString(ReceiveByte_HFCT);
                }

                if (this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        txt_ReceiveMsg.AppendText($"\n Receive From {client.RemoteEndPoint.ToString() + " " + DateTime.Now.ToString()}  : \n");

                        txt_ReceiveMsg.AppendText(DataRecv + "\n");

                        txt_ReceiveMsg.SelectionStart = txt_ReceiveMsg.Text.Length;

                        txt_ReceiveMsg.ScrollToCaret();
                    }));
                }

            }
            catch (Exception ex)
            {
                WriteLog(ex);
                Thread thr_ReConnecting = new Thread(() =>
                  {
                      if (client != null && client.Connected)
                      {
                          IPEndPoint temppoint = (IPEndPoint)client.RemoteEndPoint;
                          socketClients.TryRemove(client.RemoteEndPoint.ToString(), out client);
                          client.Shutdown(SocketShutdown.Both);
                          client.Close();
                          bool isConnected = false;
                          do
                          {
                              try
                              {
                                  Socket tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                                  tempSocket.Connect(temppoint);
                                  isConnected = true;
                                  socketClients.TryAdd(tempSocket.RemoteEndPoint.ToString(), tempSocket);
                              }
                              catch (Exception)
                              {
                                  continue;
                              }

                          } while (!isConnected);
                      }
                  });
                thr_ReConnecting.IsBackground = true;
                thr_ReConnecting.Start();
            }
        }

        //弃用的重连策略
        public void ReConnet(string IpAddress, int Port)//接收参数是目标ip地址和目标端口号
        {
            Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            bool IsConnet = false;

            Thread thread = new Thread(() =>
            {
                while (!IsConnet)//循环
                {
                    try
                    {
                        client.Connect(IPAddress.Parse(IpAddress), Port);//尝试连接，失败则会跳去catch
                        socketClients.TryAdd(client.RemoteEndPoint.ToString(), client);
                        client.BeginReceive(ReceiveByte_HFCT, 0, ReceiveByte_HFCT.Length, SocketFlags.None, new AsyncCallback(AsyncReceiveCall), client);
                        IsConnet = false;//成功连接后修改bool值为false,这样下一步循环就不再执行。
                        break;
                    }
                    catch (SocketException)
                    {
                        if (socketClients.Count > 0 && socketClients.ContainsKey(client.RemoteEndPoint.ToString()))
                        {
                            socketClients.TryRemove(client.RemoteEndPoint.ToString(), out client);
                        }
                        client.Shutdown(SocketShutdown.Both);
                        client.Close();
                        /*使用新的客户端资源覆盖，上一个已经废弃。如果继续使用以前的资源进行连接，
                        即使参数正确， 服务器全部打开也会无法连接*/
                        client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        Thread.Sleep(1000);
                    }
                }
            });
            thread.IsBackground = true;//设置为后台线程，在程序退出时自己会自动释放
            thread.Start();//开始执行线程
        }

        /// <summary>
        /// 16进制字符串转byte数组
        /// </summary>
        public static byte[] hexStringToByteArray(string data)
        {
            string[] chars = data.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] returnBytes = new byte[chars.Length];
            //逐个字符变为16进制字节数据
            for (int i = 0; i < chars.Length; i++)
            {
                returnBytes[i] = Convert.ToByte(chars[i], 16);
            }
            return returnBytes;
        }

        /// <summary>
        /// byte数组转16进制字符串
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static string byteArrayToHexString(byte[] data)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                builder.Append(string.Format("{0:X2} ", data[i]));
            }
            return builder.ToString().Trim();
        }

        /// <summary>
        /// 生成已知信息表
        /// </summary>
        private void GenerateDataTable()
        {
            dt_ServerInfo.Columns.Add("ServerIp");
            dt_ServerInfo.Columns.Add("ServerPort");
            dt_ServerInfo.Columns.Add("Command");
            DataRow dr1 = dt_ServerInfo.NewRow();
            dr1["ServerIp"] = "10.10.1.107";
            dr1["ServerPort"] = "10010";
            dr1["Command"] = "68 01 C1 00 00 08 00 02 00 00 34 16";
            dt_ServerInfo.Rows.Add(dr1);
            DataRow dr2 = dt_ServerInfo.NewRow();
            dr2["ServerIp"] = "10.10.1.107";
            dr2["ServerPort"] = "10011";
            dr2["Command"] = "68 02 00 00 00 01 00 06 00 00 71 16";
            dt_ServerInfo.Rows.Add(dr2);
        }

        #endregion


        #region Socket 心跳检测
        /// <summary>
        /// IOControl设置的数据
        /// keep-alive 每三秒发送一次，如果对方没有相应，每0.5秒发送一次确认
        /// 如果连续3次没有回应，就抛出异常移除连接
        /// </summary>
        /// <returns></returns>
        private byte[] GetKeepLiveData()
        {
            uint dummy = 0;
            byte[] inOptionValues = new byte[Marshal.SizeOf(dummy) * 3];
            BitConverter.GetBytes((uint)1).CopyTo(inOptionValues, 0);
            BitConverter.GetBytes((uint)3000).CopyTo(inOptionValues, Marshal.SizeOf(dummy)); //keep-alive 间隔
            BitConverter.GetBytes((uint)500).CopyTo(inOptionValues, Marshal.SizeOf(dummy) * 2); //尝试间隔
            return inOptionValues;
        }

        private void StartCheckAlive()
        {
            Thr_KeepAllive = new Thread(new ThreadStart(CheckAlive))
            {
                IsBackground = true
            };
            Thr_KeepAllive.Start();
        }

        private void StartReceiveData()
        {
            Thr_Receive = new Thread(new ThreadStart(ReceiveData))
            {
                IsBackground = true
            };
            Thr_Receive.Start();
        }
        private void ReceiveData()
        {
            while (true)
            {
                if (!Stopflag)
                {
                    try
                    {
                        foreach (var item in socketClients)
                        {
                            /*----------------------------------------------关于接收数据所用的字节数组长度，假定在实际使用时有标志来告知回数长度----------------------------*/
                            if (item.Value.Connected)
                            {
                                if (item.Key.Contains("10010"))
                                {
                                    item.Value.BeginReceive(ReceiveByte_HFCT, 0, ReceiveByte_HFCT.Length, SocketFlags.None, new AsyncCallback(AsyncReceiveCall), item.Value);
                                }
                                else
                                {
                                    item.Value.BeginReceive(ReceiveByte_CIRC, 0, ReceiveByte_CIRC.Length, SocketFlags.None, new AsyncCallback(AsyncReceiveCall), item.Value);
                                }
                            }
                        }
                        DateTime now = DateTime.Now;
                        while (now.AddSeconds(2) > DateTime.Now) { }
                    }
                    catch (Exception)  //Socket已关闭
                    {
                        continue;
                    }
                }
            }
        }
        private void CheckAlive()
        {
            Thread.Sleep(10000000);
            for (int i = 0; i < dt_ServerInfo.Rows.Count; i++)
            {
                string testEndPoint = dt_ServerInfo.Rows[i]["ServerIP"].ToString() + ":" + dt_ServerInfo.Rows[i]["ServerPort"].ToString();
                bool isExist = false;
                foreach (var item in socketClients)
                {
                    if (item.Key.Contains(testEndPoint))
                    {
                        isExist = true;
                    }
                }
                if (isExist == false)       //如果他没有
                {
                    do
                    {
                        try
                        {
                            Socket socekt = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                            socekt.Connect(IPAddress.Parse(dt_ServerInfo.Rows[i]["ServerIP"].ToString()), int.Parse(dt_ServerInfo.Rows[i]["ServerPort"].ToString()));
                            socketClients.TryAdd(socekt.RemoteEndPoint.ToString(), socekt);
                            isExist = true;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    } while (!isExist);
                }
            }

        }

        #endregion

        private void materialButton1_Click(object sender, EventArgs e)
        {
            Thr_Send = new Thread(new ThreadStart(SendThread));
            Thr_Send.IsBackground = true;
            Thr_Send.Start();
        }

        private static void FlushMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
            }
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            this.Dispose();
            FlushMemory();
        }

        private void materialButton2_Click(object sender, EventArgs e)
        {
            if (materialButton2.Text == "Stop")
            {
                Stopflag = true;
                materialButton2.Text = "Start";
            }
            else
            {
                Stopflag = false;
                materialButton2.Text = "Stop";
            }

        }

        private static void WriteLog(Exception ex)
        {
            string strDataInfo = "程序异常：" + DateTime.Now + "\r\n";
            string strException = string.Format(strDataInfo + $"异常类型：{ex.GetType().Name}\r\n" +
                $"异常消息：{ex.Message}\r\n+" +
                $"异常信息：{ex.StackTrace}");
            //默认存储路径为Debug目录下的ErrLog文件夹
            if (!Directory.Exists("ErrLog"))
            {
                Directory.CreateDirectory("ErrLog");
            }
            using (StreamWriter sw = new StreamWriter(@"ErrLog\ErrLog.txt", true))
            {
                sw.WriteLine(strException);
                sw.WriteLine("---------------------------------------------------------------------");
            }
        }

        private void ReConnceting(Socket errorsocket)
        {
            try
            {
                IPEndPoint endPoint = (IPEndPoint)errorsocket.RemoteEndPoint;
                Socket tempsocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                errorsocket.Shutdown(SocketShutdown.Both);
                errorsocket.Close();
                if (socketClients.ContainsKey(endPoint.ToString()))
                {
                    try
                    {
                        socketClients.TryRemove(errorsocket.RemoteEndPoint.ToString(), out Socket Temp_socket);
                    }
                    catch (Exception) { }
                }
                DateTime now = DateTime.Now;
                while (now.AddSeconds(3) > DateTime.Now) { }
                tempsocket.Connect(endPoint);
                if (tempsocket.Connected)
                {
                    socketClients.TryAdd(tempsocket.RemoteEndPoint.ToString(), tempsocket);
                }
            }
            catch (SocketException)
            { }
        }
    }
}
