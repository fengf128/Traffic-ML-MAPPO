using UnityEngine;
using System.Collections;
using System.Collections.Generic;




namespace HealthbarGames
{
    public class TrafficLightManager : MonoBehaviour
    {

        #region Default phase timings
        // we use this parameters only as serialized fields in custom traffic manager's editor
        // (TrafficLightManagerEditor) so we disable warning 0414 because compiler
        // detects this as not used variables
#pragma warning disable 0414
        // default phase start time that is used to initialize new phases
        [SerializeField]
        private float DefaultPhaseStartTime = 2.0f;

        // default phase active time that is used to initialize new phases
        [SerializeField]
        private float DefaultPhaseActiveTime = 10.0f;

        // default phase end time that is used to initialize new phases
        [SerializeField]
        private float DefaultPhaseEndTime = 2.0f;
#pragma warning restore 0414
        #endregion

        // delay between end of curent phase and start of next phase
        [SerializeField]
        private float PhaseDelay = 1.0f;

        // yellow light blink frequency (x times per second)
        [SerializeField]
        private float YellowBlinkFreq = 1.0f;

        // list of all phases for this traffic light manager (phases sequence)
        [SerializeField]
        private List<TrafficLightPhase> PhaseList;

        // defined programs (Main - normal work, Malfunction - yellow light blinking)
        public enum Program { None, Main, Malfunction };

        // initial program - used when scene is started
        [SerializeField]
        private Program InitialProgram = Program.Main;

        // current program
        private Program mCurrentProgram = Program.None;

        // index of currently active phase (phase that currently goes from 'Stop' state to 'Go' state and again to 'Stop' state)
        private int mCurrentPhaseIndex;

        // currently active phase
        private TrafficLightPhase mCurrentPhase;

        // 标记是否正在进行灯色切换过渡（防止 RL 指令频繁穿插导致协程崩溃）
        private bool mIsTransitioning = false;


        void Start()
        {
            // after scene start load initial program
            ChangeProgram(InitialProgram);
        }

        // gets current program
        public Program GetProgram()
        {
            return mCurrentProgram;
        }
        // =============================
        // RL 控制接口：切换相位
        // =============================
        public void SetNSGreen()
        {
            SetPhaseByIndex(0);
        }

        public void SetEWGreen()
        {
            SetPhaseByIndex(1);
        }

        public void SetNSLeftGreen()
        {
            SetPhaseByIndex(2);
        }

        public void SetEWLeftGreen()
        {
            SetPhaseByIndex(3);
        }

        // 核心切换逻辑
        private void SetPhaseByIndex(int phaseIndex)
        {
            // 1. 如果没有相位或索引非法，直接返回
            if (PhaseList == null || PhaseList.Count == 0) return;
            if (phaseIndex < 0 || phaseIndex >= PhaseList.Count) return;

            // 2. 【防抖保护】：如果正在执行"黄->红->绿"的过渡期，忽略新指令
            if (mIsTransitioning) return;

            // 3. 【状态保持 - 实现无限长绿灯的核心】：
            // 如果 RL 连续下发相同的目标相位，且当前该相位已经是绿灯，什么都不做！
            // 这使得绿灯可以跨越多个 RL Step 持续亮起，直到 RL 决定切换。
            if (mCurrentPhaseIndex == phaseIndex && mCurrentPhase != null && mCurrentPhase.GetState() == TrafficLightBase.State.Go)
            {
                return;
            }

            // 4. 指令发生变化，停止之前的默认死循环程序，开启安全切换协程
            StopAllCoroutines();
            StartCoroutine(TransitionToPhaseCo(phaseIndex));
        }

        // 安全切换相位的协程（强制 1.5s 黄灯 + 1.5s 全红 = 3s 切换成本）
        private IEnumerator TransitionToPhaseCo(int targetPhaseIndex)
        {
            mIsTransitioning = true;

            // 第一步：失去路权的方向亮黄灯
            if (mCurrentPhase != null && mCurrentPhase.GetState() == TrafficLightBase.State.Go)
            {
                mCurrentPhase.SetState(TrafficLightBase.State.PrepareToStop);
                // 强制限制黄灯为 1.5 秒，保证与 5 秒的 RL 决策窗完美配合
                yield return new WaitForSeconds(1.5f);
            }

            // 第二步：全红真空期 (All-Red Clearance Phase)
            SetAllPhasesTo(TrafficLightBase.State.Stop);
            // 强制 1.5 秒全红，让已经进入十字路口中心的车辆安全驶离，彻底消灭侧撞死锁
            yield return new WaitForSeconds(1.5f);

            // 第三步：新获得路权的方向直接亮绿灯
            mCurrentPhaseIndex = targetPhaseIndex;
            mCurrentPhase = PhaseList[mCurrentPhaseIndex];
            mCurrentPhase.SetState(TrafficLightBase.State.Go);

            mIsTransitioning = false;
        }
        // stops currently working program and loads new program
        public void ChangeProgram(Program program)
        {
            StopAllCoroutines();
            mCurrentProgram = program;
            switch (mCurrentProgram)
            {
                case Program.Main:
                    StartCoroutine(MainProgramCo());
                    break;

                case Program.Malfunction:
                    StartCoroutine(YellowBlinkProgramCo());
                    break;

                default:
                    StartCoroutine(YellowBlinkProgramCo());
                    break;
            }
        }

        // coroutine for main program
        private IEnumerator MainProgramCo()
        {
            // select first phase from list
            mCurrentPhaseIndex = 0;
            mCurrentPhase = PhaseList[0];

            // begin with all traffic lights modules set to 'Stop' state (red lights)
            SetAllPhasesTo(TrafficLightBase.State.Stop);
            while (true)
            {
                // set current phase to 'PrepareToGo' state (red and yellow lights)
                mCurrentPhase.SetState(TrafficLightBase.State.PrepareToGo);
                // wait for end of state
                yield return new WaitForSeconds(mCurrentPhase.PhaseStartTime);

                // set current phase to 'Go' state (green lights)
                mCurrentPhase.SetState(TrafficLightBase.State.Go);
                // wait for end of state
                yield return new WaitForSeconds(mCurrentPhase.PhaseActiveTime);

                // set current phase to 'PrepareToStop' state (yellow lights)
                mCurrentPhase.SetState(TrafficLightBase.State.PrepareToStop);
                // wait for end of state
                yield return new WaitForSeconds(mCurrentPhase.PhaseEndTime);

                // set current phase to 'Stop' state (red lights)
                mCurrentPhase.SetState(TrafficLightBase.State.Stop);

                // this phase has ended so make delay between phases before nex phase will be started
                yield return new WaitForSeconds(PhaseDelay);

                // calculate next phase index
                mCurrentPhaseIndex++;
                mCurrentPhaseIndex = mCurrentPhaseIndex % PhaseList.Count;

                // select next phase and continue program's loop
                mCurrentPhase = PhaseList[mCurrentPhaseIndex];
            }
        }

        // sets all phases (and coresponding traffic lights) to specific state
        private void SetAllPhasesTo(TrafficLightBase.State state)
        {
            foreach (TrafficLightPhase phase in PhaseList)
            {
                phase.SetState(state);
            }
        }


        // coroutine for malfunction (yellow light blinking) program
        private IEnumerator YellowBlinkProgramCo()
        {
            // set all phases and coresponding traffic light modules to yellow blinking state
            SetAllPhasesTo(TrafficLightBase.State.YellowBlink);
            // calculate blink delay based on blink frequency
            float blinkDelay = (YellowBlinkFreq > 0.0f) ? 1.0f / YellowBlinkFreq : 1000.0f;
            bool blinkState = false;
            while (true)
            {
                // change all yellow lights state to opposite
                blinkState = !blinkState;
                foreach (TrafficLightPhase phase in PhaseList)
                {
                    phase.YellowBlink(blinkState);
                }
                // now wait for calculated amount of time between each yellow blink
                yield return new WaitForSeconds(blinkDelay);
            }
        }
    }

}
