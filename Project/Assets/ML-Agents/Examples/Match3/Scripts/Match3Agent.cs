using System;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Extensions.Match3;

namespace Unity.MLAgentsExamples
{
    enum State
    {
        Invalid = -1,

        FindMatches = 0,
        ClearMatched = 1,
        Drop = 2,
        FillEmpty = 3,

        WaitForMove = 4,
    }

    public class Match3Agent : Agent
    {
        [HideInInspector]
        public Match3Board Board;

        public float MoveTime = 1.0f;
        public int MaxMoves = 500;

        State m_CurrentState = State.WaitForMove;
        float m_TimeUntilMove;
        private int m_MovesMade;

        private System.Random m_Random;
        private const float k_RewardMultiplier = 0.01f;

        void Awake()
        {
            Board = GetComponent<Match3Board>();
            var seed = Board.RandomSeed == -1 ? gameObject.GetInstanceID() : Board.RandomSeed + 1;
            m_Random = new System.Random(seed);
        }

        public override void OnEpisodeBegin()
        {
            base.OnEpisodeBegin();

            Board.InitSettled();
            m_CurrentState = State.FindMatches;
            m_TimeUntilMove = MoveTime;
            m_MovesMade = 0;
        }

        private void FixedUpdate()
        {
            if (Academy.Instance.IsCommunicatorOn)
            {
                FastUpdate();
            }
            else
            {
                AnimatedUpdate();
            }

            // We can't use the normal MaxSteps system to decide when to end an episode,
            // since different agents will make moves at different frequencies (depending on the number of
            // chained moves). So track a number of moves per Agent and manually interrupt the episode.
            if (m_MovesMade >= MaxMoves)
            {
                EpisodeInterrupted();
            }
        }

        void FastUpdate()
        {
            while (true)
            {
                var hasMatched = Board.MarkMatchedCells();
                if (!hasMatched)
                {
                    break;
                }
                var pointsEarned = Board.ClearMatchedCells();
                AddReward(k_RewardMultiplier * pointsEarned);
                Board.DropCells();
                Board.FillFromAbove();
            }

            while (true)
            {
                // Shuffle the board until we have a valid move.
                bool hasMoves = HasValidMoves();
                if (hasMoves)
                {
                    break;
                }
                Board.InitSettled();
            }
            RequestDecision();
            m_MovesMade++;
        }

        void AnimatedUpdate()
        {
            m_TimeUntilMove -= Time.deltaTime;
            if (m_TimeUntilMove > 0.0f)
            {
                return;
            }

            m_TimeUntilMove = MoveTime;

            var nextState = State.Invalid;
            switch (m_CurrentState)
            {
                case State.FindMatches:
                    var hasMatched = Board.MarkMatchedCells();
                    nextState = hasMatched ? State.ClearMatched : State.WaitForMove;
                    if (nextState == State.WaitForMove)
                    {
                        m_MovesMade++;
                    }
                    break;
                case State.ClearMatched:
                    var pointsEarned = Board.ClearMatchedCells();
                    AddReward(k_RewardMultiplier * pointsEarned);
                    nextState = State.Drop;
                    break;
                case State.Drop:
                    Board.DropCells();
                    nextState = State.FillEmpty;
                    break;
                case State.FillEmpty:
                    Board.FillFromAbove();
                    nextState = State.FindMatches;
                    break;
                case State.WaitForMove:
                    while (true)
                    {
                        // Shuffle the board until we have a valid move.
                        bool hasMoves = HasValidMoves();
                        if (hasMoves)
                        {
                            break;
                        }
                        Board.InitSettled();
                    }
                    RequestDecision();

                    nextState = State.FindMatches;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            m_CurrentState = nextState;
        }

        bool HasValidMoves()
        {
            foreach (var move in Board.ValidMoves())
            {
                return true;
            }

            return false;
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discreteActions = actionsOut.DiscreteActions;
            discreteActions[0] = GreedyBestMove();
        }

        int GreedyBestMove()
        {
            var pointsByType = new[] { Board.BasicCellPoints, Board.SpecialCell1Points, Board.SpecialCell2Points };

            var bestMoveIndex = 0;
            var bestMovePoints = -1;
            var numMovesAtCurrentScore = 0;

            foreach (var move in Board.ValidMoves())
            {
                var movePoints = EvalMovePoints(move, pointsByType);
                if (movePoints < bestMovePoints)
                {
                    // Worse, skip
                    continue;
                }

                if (movePoints > bestMovePoints)
                {
                    // Better, keep
                    bestMovePoints = movePoints;
                    bestMoveIndex = move.MoveIndex;
                    numMovesAtCurrentScore = 1;
                }
                else
                {
                    // Tied for best - use reservoir sampling to make sure we select from equal moves uniformly.
                    // See https://en.wikipedia.org/wiki/Reservoir_sampling#Simple_algorithm
                    numMovesAtCurrentScore++;
                    var randVal = m_Random.Next(0, numMovesAtCurrentScore);
                    if (randVal == 0)
                    {
                        // Keep the new one
                        bestMoveIndex = move.MoveIndex;
                    }
                }
            }

            return bestMoveIndex;
        }

        int EvalMovePoints(Move move, int[] pointsByType)
        {
            // Counts the expected points for making the move.
            var moveVal = Board.GetCellType(move.Row, move.Column);
            var moveSpecial = Board.GetSpecialType(move.Row, move.Column);
            var (otherRow, otherCol) = move.OtherCell();
            var oppositeVal = Board.GetCellType(otherRow, otherCol);
            var oppositeSpecial = Board.GetSpecialType(otherRow, otherCol);


            int movePoints = EvalHalfMove(
                otherRow, otherCol, moveVal, moveSpecial, move.Direction, pointsByType
            );
            int otherPoints = EvalHalfMove(
                move.Row, move.Column, oppositeVal, oppositeSpecial, move.OtherDirection(), pointsByType
            );
            return movePoints + otherPoints;
        }

        int EvalHalfMove(int newRow, int newCol, int newValue, int newSpecial, Direction incomingDirection, int[] pointsByType)
        {
            // This is a essentially a duplicate of AbstractBoard.CheckHalfMove but also counts the points for the move.
            int matchedLeft = 0, matchedRight = 0, matchedUp = 0, matchedDown = 0;
            int scoreLeft = 0, scoreRight = 0, scoreUp = 0, scoreDown = 0;

            if (incomingDirection != Direction.Right)
            {
                for (var c = newCol - 1; c >= 0; c--)
                {
                    if (Board.GetCellType(newRow, c) == newValue)
                    {
                        matchedLeft++;
                        scoreLeft += pointsByType[Board.GetSpecialType(newRow, c)];
                    }
                    else
                        break;
                }
            }

            if (incomingDirection != Direction.Left)
            {
                for (var c = newCol + 1; c < Board.Columns; c++)
                {
                    if (Board.GetCellType(newRow, c) == newValue)
                    {
                        matchedRight++;
                        scoreRight += pointsByType[Board.GetSpecialType(newRow, c)];
                    }
                    else
                        break;
                }
            }

            if (incomingDirection != Direction.Down)
            {
                for (var r = newRow + 1; r < Board.Rows; r++)
                {
                    if (Board.GetCellType(r, newCol) == newValue)
                    {
                        matchedUp++;
                        scoreUp += pointsByType[Board.GetSpecialType(r, newCol)];
                    }
                    else
                        break;
                }
            }

            if (incomingDirection != Direction.Up)
            {
                for (var r = newRow - 1; r >= 0; r--)
                {
                    if (Board.GetCellType(r, newCol) == newValue)
                    {
                        matchedDown++;
                        scoreDown += pointsByType[Board.GetSpecialType(r, newCol)];
                    }
                    else
                        break;
                }
            }

            if ((matchedUp + matchedDown >= 2) || (matchedLeft + matchedRight >= 2))
            {
                // It's a match. Start from counting the piece being moved
                var totalScore = pointsByType[newSpecial];
                if (matchedUp + matchedDown >= 2)
                {
                    totalScore += scoreUp + scoreDown;
                }

                if (matchedLeft + matchedRight >= 2)
                {
                    totalScore += scoreLeft + scoreRight;
                }
                return totalScore;
            }

            return 0;
        }
    }

}
