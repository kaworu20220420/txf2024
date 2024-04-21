using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace txf2024
{
	public partial class Form1 : Form
	{
		public const int MAGIC_SEND = 0x53454e44; // "SEND"
		public const int MAGIC_RCVD = 0x72637664; // "rcvd"
		public const int FILENAME_LEN = 20;
		public const int BLOCKSIZE = 1024;
		public const int MAX_FILE_SIZE = 0x7fffffff;

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		public struct TxfHeader
		{
			public uint Magic;
			public uint Filesize; // big endian
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = FILENAME_LEN)]
			public byte[] Filename;
			public byte FilenameTerm; // must be zero
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
			public byte[] Unused;
			public byte[] ToBytes()
			{
				// Create a new byte array
				byte[] bytes = new byte[Marshal.SizeOf(typeof(TxfHeader))];

				// Create a pin handle for the TxfHeader
				GCHandle pinStructure = GCHandle.Alloc(this, GCHandleType.Pinned);

				try
				{
					// Get a pointer to the TxfHeader
					IntPtr pointer = pinStructure.AddrOfPinnedObject();

					// Copy the TxfHeader to the byte array
					Marshal.Copy(pointer, bytes, 0, bytes.Length);

					return bytes;
				}
				finally
				{
					// Free the pin handle
					pinStructure.Free();
				}
			}

			public static TxfHeader? FromBytes(byte[] bytes)
			{
				GCHandle pinArray = GCHandle.Alloc(bytes, GCHandleType.Pinned);

				try
				{
					// Get the address of the pinned byte array
					IntPtr pointer = pinArray.AddrOfPinnedObject();

					// Convert the bytes to the structure
					object? structure = Marshal.PtrToStructure(pointer, typeof(TxfHeader)); // ★ null チェックのために一時変数に格納

					if (structure is TxfHeader header) // ★ null チェック
					{
						return header;
					}
					else
					{
						Console.WriteLine("Failed to convert bytes to TxfHeader.");
						return null;
					}
				}
				finally
				{
					// Free the pin handle
					pinArray.Free();
				}
			}
		}
		public delegate TxfTxWorkArea TxfInitDelegate(string arg);
		public delegate int TxfProcessDelegate(Socket socket, TxfTxWorkArea handle);
		public delegate void TxfFinishDelegate(TxfTxWorkArea handle);
		public struct TxfWorkingSet
		{
			public TxfInitDelegate Init;
			public TxfProcessDelegate Process;
			public TxfFinishDelegate Finish;

			public TxfWorkingSet(TxfInitDelegate init, TxfProcessDelegate process, TxfFinishDelegate finish)
			{
				Init = init;
				Process = process;
				Finish = finish;
			}
		}

		public struct TxfTxWorkArea
		{
			public FileStream Fd;
			public int Size;
			public TxfHeader H;
		}

		public Form1(string[] args)
		{
			Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(args[0]), int.Parse(args[1]));

			TxfWorkingSet rxSet = new TxfWorkingSet(RxInit, RxProcess, RxFinish);
			TxfWorkingSet txSet = new TxfWorkingSet(TxInit, TxProcess, TxFinish);

			if (args.Length == 2)
			{
				// tx-server/rx-client mode
				Server(socket, endPoint, string.Empty, txSet);
			}
			else
			{
				// rx-server/tx-client mode
				Server(socket, endPoint, args[2], rxSet);
			}

			socket.Close();
		}

		private int SendBlock(Socket socket, byte[] buffer, int size)
		{
			int pos = 0;
			int wsize;

			while (pos < size)
			{
				wsize = socket.Send(buffer, pos, size - pos, SocketFlags.None);
				if (wsize < 0)
					break;
				pos += wsize;
			}

			return pos;
		}

		private int RecvBlock(Socket socket, byte[] buffer, int size)
		{
			int pos = 0;
			int rsize;

			while (pos < size)
			{
				rsize = socket.Receive(buffer, pos, size - pos, SocketFlags.None);
				if (rsize < 0)
					break;
				pos += rsize;
			}

			return pos;
		}

		private string GetFilename(string filename)
		{
			const char DELIMITER = '/';

			int i, len;
			string p, q;

			/* find the last delimiter character */
			len = filename.Length;
			for (i = len - 1; i >= 0; i--)
			{
				if (filename[i] == DELIMITER)
					break;
			}

			/* filename starts after delimiter */
			p = filename.Substring(i + 1);
			q = "";

			/* convert filename to ASCII */
			for (i = 0; i < FILENAME_LEN; i++)
			{
				if (i >= p.Length || p[i] == DELIMITER)
					break;

				/* multi-byte character is not supported */
				if (p[i] == DELIMITER)
					q += '_';
				else
					q += p[i];
			}

			return q;
		}

		private string ConvertPath(string inPath, int maxlen)
		{
			int len;
			string outPath = "";

			len = inPath.Length;
			if (len > maxlen - 1)
				len = maxlen - 1;

			while (len-- > 0)
			{
				switch (inPath[0])
				{
					case '/':
						outPath += '\\';
						break;
					case ':':
						outPath += '.';
						break;
					case '%':
						inPath = inPath.Substring(1);
						len--;
						if (len > 0)
							outPath += inPath[0];
						break;
					default:
						outPath += inPath[0];
						break;
				}
				inPath = inPath.Substring(1);
			}

			return outPath;
		}

		private TxfTxWorkArea RxInit(string arg) => new TxfTxWorkArea();

		private int RxProcess(Socket socket, TxfTxWorkArea handle)
		{
			int i, remain;
			byte[] buf = new byte[BLOCKSIZE];
			TxfHeader h = new TxfHeader();
			string tmp;

			if (RecvBlock(socket, h.ToBytes(), Marshal.SizeOf(typeof(TxfHeader))) < Marshal.SizeOf(typeof(TxfHeader)))
			{
				Console.WriteLine("RxProcess: RecvBlock (header)");
				return -1;
			}

			if (h.Magic != MAGIC_SEND)
			{
				Console.WriteLine("RxProcess: invalid header");
				return -1;
			}

			h.FilenameTerm = 0;
			int size = (int)h.Filesize;
			tmp = h.Filename ==null ? string.Empty : Encoding.ASCII.GetString(h.Filename);

			Console.WriteLine("{0}, {1} byte", tmp, size);

			FileStream? fs = null;
			try
			{
				fs = File.Create(tmp);

				for (i = 0; i < size; i += BLOCKSIZE)
				{
					remain = size - i;
					if (remain > BLOCKSIZE)
						remain = BLOCKSIZE;

					if (RecvBlock(socket, buf, remain) < remain)
					{
						Console.WriteLine("RxProcess: RecvBlock (data)");
						return -1;
					}

					fs.Write(buf, 0, remain);
				}

				h.Magic = MAGIC_RCVD;
				if (SendBlock(socket, h.ToBytes(), Marshal.SizeOf(typeof(TxfHeader))) < Marshal.SizeOf(typeof(TxfHeader)))
				{
					Console.WriteLine("RxProcess: SendBlock (ack)");
					return -1;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("RxProcess: {0}", e.Message);
				return -1;
			}
			finally
			{
				if (fs != null)
					fs.Close();
			}

			return 0;
		}

		private void RxFinish(TxfTxWorkArea handle)
		{
			// Implementation of RxFinish
		}

		private TxfTxWorkArea TxInit(string filename)
		{
			TxfTxWorkArea wk = new TxfTxWorkArea();
			string path = ConvertPath(filename, FILENAME_LEN);
			string fn = GetFilename(path);

			if (string.IsNullOrEmpty(fn))
			{
				Console.WriteLine("TxInit: invalid file name");
				return wk;
			}

			try
			{
				wk.Fd = File.OpenRead(path);
				wk.Size = (int)wk.Fd.Length;

				wk.H = new TxfHeader();
				wk.H.Magic = MAGIC_SEND;
				wk.H.Filesize = (uint)wk.Size;
				wk.H.Filename = Encoding.ASCII.GetBytes(fn);

				Console.WriteLine("{0}, {1} byte", fn, wk.Size);
			}
			catch (Exception e)
			{
				Console.WriteLine("TxInit: {0}", e.Message);
				return wk;
			}

			return wk;
		}

		private int TxProcess(Socket socket, TxfTxWorkArea wk)
		{
			int i, remain;
			byte[] buf = new byte[BLOCKSIZE];

			if (SendBlock(socket, wk.H.ToBytes(), Marshal.SizeOf(typeof(TxfHeader))) < Marshal.SizeOf(typeof(TxfHeader)))
			{
				Console.WriteLine("TxProcess: SendBlock (header)");
				return -1;
			}

			for (i = 0; i < wk.Size; i += BLOCKSIZE)
			{
				remain = wk.Size - i;
				if (remain > BLOCKSIZE)
					remain = BLOCKSIZE;

				wk.Fd.Read(buf, 0, remain);

				if (SendBlock(socket, buf, remain) < remain)
				{
					Console.WriteLine("TxProcess: SendBlock (data)");
					return -1;
				}
			}

			if (RecvBlock(socket, buf, Marshal.SizeOf(typeof(TxfHeader))) < Marshal.SizeOf(typeof(TxfHeader)))
			{
				Console.WriteLine("TxProcess: RecvBlock (ack)");
				return -1;
			}

			TxfHeader? h = TxfHeader.FromBytes(buf);
			if (h == null) { return -1; }
			if (h?.Magic != MAGIC_RCVD)
			{
				Console.WriteLine("TxProcess: invalid ack");
				return -1;
			}

			return 0;
		}

		private void TxFinish(TxfTxWorkArea wk) =>	wk.Fd.Close();

		private int Client(Socket socket, IPEndPoint endPoint, string arg, TxfWorkingSet work)
		{
			TxfTxWorkArea handle;

			Console.WriteLine("* client");

			handle = work.Init(arg);

			try
			{
				socket.Connect(endPoint);
				Console.WriteLine("connected to {0}", endPoint.Address);

				if (work.Process(socket, handle) != 0)
				{
					Console.WriteLine("client: process");
					work.Finish(handle);
					return -1;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("client: {0}", e.Message);
				work.Finish(handle);
				return -1;
			}

			work.Finish(handle);
			return 0;
		}

		private int Server(Socket socket, IPEndPoint endPoint, string arg, TxfWorkingSet work)
		{
			TxfTxWorkArea? handle; // ★ null許容型を使用

			Console.WriteLine("* server");

			handle = work.Init(arg);
			if (handle == null) // ★ nullチェックを追加
			{
				Console.WriteLine("server: init");
				return -1;
			}

			socket.Bind(endPoint);
			socket.Listen(1);

			IPEndPoint? localEndPoint = socket.LocalEndPoint as IPEndPoint;
			if (localEndPoint != null) // ★ nullチェックを追加
			{
				Console.WriteLine("address {0} port {1}", localEndPoint.Address, localEndPoint.Port);
			}

			Socket clientSocket = socket.Accept();
			IPEndPoint? peer = clientSocket.RemoteEndPoint as IPEndPoint;
			if (peer != null) // ★ nullチェックを追加
			{
				Console.WriteLine("connected from {0} port {1}", peer.Address, peer.Port);
			}

			if (work.Process(clientSocket, handle.Value) != 0) // ★ null許容型から値を取り出す
			{
				Console.WriteLine("server: process");
				clientSocket.Close();
				work.Finish(handle.Value); // ★ null許容型から値を取り出す
				return -1;
			}

			clientSocket.Close();
			work.Finish(handle.Value); // ★ null許容型から値を取り出す

			return 0;
		}


	}
}
