using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Threading.Timer;

namespace MessageLoop
{
	public static class Program
	{
		private static MessageLoop messageLoop;
		private static void tick(object state)
		{
			var id = Thread.CurrentThread.ManagedThreadId;

			messageLoop.Invoke(new Action(() => Console.WriteLine("From {0} in {1}", id, Thread.CurrentThread.ManagedThreadId)));
		}

		[STAThread]
		public static void Main()
		{
			using (messageLoop = new MessageLoop())
			using (var timer = new Timer(tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
			{
				Console.ReadLine();
			}
		}
	}
	public class MessageLoopAsyncResult : IAsyncResult
	{
		private object state;

		internal Exception Exception
		{
			get;
			set;
		}

		public WaitHandle AsyncWaitHandle
		{
			get;
			internal set;
		}
		public bool CompletedSynchronously
		{
			get
			{
				return IsCompleted;
			}
		}
		public bool IsCompleted
		{
			get;
			set;
		}
		public object AsyncState
		{
			get
			{
				if (Exception != null) throw Exception;
				return state;
			}
			internal set
			{
				state = value;
			}
		}
	}
	public class MessageLoop : IDisposable, ISynchronizeInvoke
	{
		private SynchronizationContext context;
		private TaskCompletionSource<bool> threadStarted;
		private Thread thread;
		private void newMessage(object state)
		{
			if (state is Tuple<Delegate, object[], TaskCompletionSource<object>>)
			{
				var message = (Tuple<Delegate, object[], TaskCompletionSource<object>>)state;

				try
				{
					message.Item3.TrySetResult(message.Item1.DynamicInvoke(message.Item2));
				}
				catch (Exception error)
				{
					message.Item3.TrySetException(error);
				}
			}
		}
		private void threadLoop(object state)
		{
			try
			{
				WindowsFormsSynchronizationContext.AutoInstall = true;

				SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

				context = SynchronizationContext.Current;

				threadStarted.TrySetResult(true);

				Application.Run();
			}
			catch (ThreadAbortException abort)
			{
				try
				{
					threadStarted.TrySetException(abort);
				}
				catch (Exception trySetException)
				{
					Debug.Write("TrySetException: " + trySetException);
				}
			}
			catch (Exception error)
			{
				try
				{
					threadStarted.TrySetException(error);
				}
				catch (Exception trySetException)
				{
					Debug.Write("TrySetException: " + trySetException);
				}
				if (OnThreadError != null) OnThreadError(error);
			}

			if (OnThreadStopped != null) OnThreadStopped();
		}

		public MessageLoop()
		{
			thread = new Thread(threadLoop);
			thread.TrySetApartmentState(ApartmentState.STA);
			threadStarted = new TaskCompletionSource<bool>();

			thread.Start();

			threadStarted.Task.Wait();
		}
		public IAsyncResult BeginInvoke(Delegate method, params object[] args)
		{
			var result = new MessageLoopAsyncResult();

			ThreadPool.QueueUserWorkItem(state =>
			{
				result.AsyncWaitHandle = new ManualResetEvent(false);
				try
				{
					result.AsyncState = Invoke(method, args);
				}
				catch (Exception exception)
				{
					result.Exception = exception;
				}
				result.IsCompleted = true;
			});

			return result;
		}
		public bool InvokeRequired
		{
			get
			{
				return thread.ManagedThreadId != Thread.CurrentThread.ManagedThreadId;
			}
		}
		public event Action OnThreadStopped;
		public event Action<Exception> OnThreadError;
		public object EndInvoke(IAsyncResult result)
		{
			if (!result.IsCompleted) result.AsyncWaitHandle.WaitOne();

			return result.AsyncState;
		}
		public object Invoke(Delegate method, params object[] args)
		{
			if (InvokeRequired)
			{
				var waiter = new TaskCompletionSource<object>();

				context.Send(newMessage, new Tuple<Delegate, object[], TaskCompletionSource<object>>(method, args, waiter));

				waiter.Task.Wait();

				return waiter.Task.Result;
			}

			return method.DynamicInvoke(args);
		}
		public void Dispose()
		{
			thread.Abort();
		}
	}
}
