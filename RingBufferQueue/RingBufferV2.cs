    /// <summary>
    /// nonblocking queue buffer
    /// </summary>
    /// <remarks>
    /// A circular buffer, circular queue, cyclic buffer or ring buffer is a data structure that uses a single, fixed-size buffer as if it were connected end-to-end. This structure lends itself easily to buffering data streams.
    /// </remarks>
    public sealed class RingBuffer<T> : IDisposable
    {
        private Action<T[]> m_onfull;
        private AutoResetEvent m_autoEvent = new AutoResetEvent(false);
        private T[] m_queueData;
        private int m_first;
        private int m_last;
        private int m_numElements;
        private int m_maxSize;
        private int m_timeoutMilliseconds;
        private Timer m_timer;
        private bool m_isDiposed = true;
        private readonly object r_timerGate = new object();
        private readonly ConcurrentQueue<Task> m_Tasks = new ConcurrentQueue<Task>();

        public RingBuffer(int maxSize, Action<T[]> onFull) : this()
        {
            if (onFull == null)
                throw new ArgumentNullException("onFull");


            if (maxSize < 1)
            {
                maxSize = 2;
            }

            m_maxSize = maxSize;
            m_queueData = new T[maxSize];

            m_timeoutMilliseconds = 0;
            m_onfull = onFull;
        }


        /// <summary>
        /// Auto flushing queue base on timeout
        /// </summary>
        public RingBuffer(int maxSize, int timeoutMilliseconds, Action<T[]> onFull) : this()
        {
            if (onFull == null)
                throw new ArgumentNullException("onFull");

            if (maxSize < 1)
            {
                maxSize = 2;
            }

            m_timeoutMilliseconds = timeoutMilliseconds;
            m_maxSize = maxSize;
            m_queueData = new T[maxSize];
            m_onfull = onFull;
            CreateBufferTimer();
        }


        private RingBuffer()
        {
            m_isDiposed = false;
            m_first = 0;
            m_last = 0;
            m_numElements = 0;
            ThreadPool.QueueUserWorkItem(TaskManager);
        }



        private void CreateBufferTimer()
        {
            lock (r_timerGate)
            {
                m_timer?.Dispose();

                if (0 >= m_timeoutMilliseconds || m_isDiposed)
                    return;

                m_timer = new Timer(OnTimerElapsed, m_autoEvent, m_timeoutMilliseconds, m_timeoutMilliseconds);
            }
        }

        private void OnTimerElapsed(object state)
        {
            lock (r_timerGate)
            {
                m_timer?.Dispose();
            }
            OnBufferFull(state);
            CreateBufferTimer();
        }


        private void OnBufferFull(object stateInfo)
        {
            T[] data = Dequeue();

            if (data?.Length > 0)
            {
                // ThreadPool.QueueUserWorkItem(OnBufferFullAsync, data);
                //m_onfull(data);

                Task task = new Task(() =>
                {
                    m_onfull(data);
                });
                m_Tasks.Enqueue(task);
            }
        }

        private const int TaskWorkerSize = 5;
        private void TaskManager(object stateInfo)
        {
            Task currentTask = null;
            List<Task> taskWorker = new List<Task>();
            do
            {
                if (!m_Tasks.IsEmpty)
                {
                    for (int i = 0; i < ((m_Tasks.Count>TaskWorkerSize ? TaskWorkerSize : m_Tasks.Count)-taskWorker.Count); i++)
                    {
                        if (m_Tasks.TryDequeue(out currentTask))
                        {
                            if (currentTask == null)
                                continue;

                            currentTask.Start();
                            taskWorker.Add(currentTask);

                        }
                    }

                    if (taskWorker.Count > 0)
                    {
                        // Task.WaitAll(taskWorker.ToArray());
                        //    taskWorker.Clear();
                        Task.WaitAny(taskWorker.ToArray());
                        taskWorker = taskWorker.Where(t => !t.IsCompleted).ToList();
                    }
                }

            } while (!m_isDiposed);//run until shutdown
        }

        /// <summary>
        /// Does the real work
        /// </summary>
        /// <param name="stateInfo">T[]</param>
        private void OnBufferFullAsync(object stateInfo)
        {
            T[] data = stateInfo as T[];

            if (data?.Length > 0)
            {
                m_onfull(data);
            }
        }

        public void Enqueue(T data)
        {
            if (data == null || m_isDiposed)
            {
                return;
            }

            lock (this)
            {
                m_queueData[m_last] = data;
                if (++m_last == m_maxSize)
                {
                    m_last = 0;
                }

                if (m_numElements < m_maxSize)
                {
                    m_numElements++;
                }
                else if (++m_first == m_maxSize)
                {
                    m_first = 0;
                }

                if (m_numElements < m_maxSize)
                {
                    // Space remaining
                    return;
                }
                else
                {
                    if (m_timer != null)
                        CreateBufferTimer();
                    //process async
                    OnBufferFull(this.m_autoEvent);
                }
            }
        }
        /// <summary>
        /// removes all data from queue
        /// </summary>
        private T[] Dequeue()
        {
            lock (this)
            {
                T[] ret = new T[m_numElements];

                if (m_numElements > 0)
                {
                    if (m_first < m_last)
                    {
                        Array.Copy(m_queueData, m_first, ret, 0, m_numElements);
                    }
                    else
                    {
                        Array.Copy(m_queueData, m_first, ret, 0, m_maxSize - m_first);
                        Array.Copy(m_queueData, 0, ret, m_maxSize - m_first, m_last);
                    }
                }

                Clear();

                return ret;
            }
        }

        /// <summary>
        /// Clear the buffer
        /// </summary>
        public void Clear()
        {
            lock (this)
            {
                // Set all the elements to null
                Array.Clear(m_queueData, 0, m_queueData.Length);

                m_first = 0;
                m_last = 0;
                m_numElements = 0;
            }
        }

        /// <summary>
        /// Resizes the buffer
        /// </summary>
        public void Resize(int newSize)
        {
            lock (this)
            {
                if (newSize < 0)
                {
                    return;
                }
                if (newSize == m_numElements)
                {
                    return; // nothing to do
                }

                T[] temp = new T[newSize];

                int loopLen = (newSize < m_numElements) ? newSize : m_numElements;

                for (int i = 0; i < loopLen; i++)
                {
                    temp[i] = m_queueData[m_first];
                    m_queueData[m_first] = default(T);

                    if (++m_first == m_numElements)
                    {
                        m_first = 0;
                    }
                }

                m_queueData = temp;
                m_first = 0;
                m_numElements = loopLen;
                m_maxSize = newSize;

                if (loopLen == newSize)
                {
                    m_last = 0;
                }
                else
                {
                    m_last = loopLen;
                }
            }
        }

        public void Dispose()
        {
            if (m_isDiposed)
                return;

            lock (this)
            {
                m_timer?.Dispose();
              //  OnBufferFull(this.m_autoEvent);
                m_isDiposed = true;

                OnBufferFull(this.m_autoEvent);//flush extra

                List<Task> taskWorker = new List<Task>();
                while (!m_Tasks.IsEmpty)
                {
                    Task currentTask = null;
                    if (m_Tasks.TryDequeue(out currentTask))
                    {
                        if (currentTask == null)
                            continue;

                        currentTask.Start();
                        taskWorker.Add(currentTask);
                    }
                }//while loop

                if(m_Tasks.Count>0)
                {
                    Task.WaitAll(taskWorker.ToArray(), 30000);
                }

            }

            GC.SuppressFinalize((object)this);
        }

        public T this[int i]
        {
            get
            {
                lock (this)
                {
                    if (i < 0 || i >= m_numElements)
                    {
                        return default(T);
                    }

                    return m_queueData[(m_first + i) % m_maxSize];
                }
            }
        }

        /// <summary>
        /// Gets the maximum size of queue buffer.
        /// </summary>
        public int MaxSize
        {
            get
            {
                lock (this)
                {
                    return m_maxSize;
                }
            }
            set
            {
                /// Setting the MaxSize will cause the buffer to resize.
                Resize(value);
            }
        }

        /// <summary>
        /// Gets the number of records in queue
        /// </summary>
        public int Length
        {
            get
            {
                lock (this)
                {
                    return m_numElements;
                }
            }
        }
    }
