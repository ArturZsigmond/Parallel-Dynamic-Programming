#include <algorithm>
#include <chrono>
#include <iostream>
#include <mutex>
#include <numeric>
#include <random>
#include <string>
#include <thread>
#include <utility>
#include <vector>

using namespace std;

inline double getA(const vector<double>& A, int i, int k, int K) { return A[i * K + k]; }
inline double getB(const vector<double>& B, int k, int j, int N) { return B[k * N + j]; }
inline double& getC(vector<double>& C, int i, int j, int N)      { return C[i * N + j]; }


static mutex g_print_mtx;

// Toggle-able debug printing (enabled by --debug flag).
static bool g_debug = false;

// For clarity when we pass work to threads.
using Task = pair<int,int>; // (row i, col j)

// Multiplies row i of A with column j of B: sum_k A[i,k]*B[k,j].
double compute_element(const vector<double>& A,
                       const vector<double>& B,
                       int i, int j, int M, int K, int N,
                       int thread_id)
{
    (void)M; // M is unused in the math itself, but kept for symmetry/clarity
    double sum = 0.0;
    for (int kk = 0; kk < K; ++kk) {
        sum += getA(A, i, kk, K) * getB(B, kk, j, N);
    }

    if (g_debug) {
        lock_guard<mutex> lk(g_print_mtx);
        cout << "compute C(" << i << "," << j << ") on thread " << thread_id << "\n";
    }
    return sum;
}

// Each thread receives a vector of (i,j) tasks and writes those entries in C.
void worker(int thread_id,
            const vector<Task>& tasks,
            const vector<double>& A, const vector<double>& B,
            vector<double>& C,
            int M, int K, int N)
{
    for (auto [i, j] : tasks) {
        getC(C, i, j, N) = compute_element(A, B, i, j, M, K, N, thread_id);
    }
}

// All return a vector sized num_threads; each entry holds that thread's tasks
// (R) Consecutive by rows (row-major linearization)
vector<vector<Task>> split_by_rows(int M, int N, int num_threads)
{
    vector<vector<Task>> res(num_threads);
    const int total = M * N;
    const int base = total / num_threads;
    const int extra = total % num_threads;

    int start = 0;
    for (int t = 0; t < num_threads; ++t) {
        int count = base + (t < extra ? 1 : 0);
        res[t].reserve(count);
        for (int idx = start; idx < start + count; ++idx) {
            int i = idx / N;
            int j = idx % N;
            res[t].push_back({i, j});
        }
        start += count;
    }
    return res;
}

// (C) Consecutive by columns (column-major linearization)
vector<vector<Task>> split_by_cols(int M, int N, int num_threads)
{
    vector<vector<Task>> res(num_threads);
    const int total = M * N;
    const int base = total / num_threads;
    const int extra = total % num_threads;

    int start = 0;
    for (int t = 0; t < num_threads; ++t) {
        int count = base + (t < extra ? 1 : 0);
        res[t].reserve(count);
        for (int idx = start; idx < start + count; ++idx) {
            int j = idx / M; // sweep columns first
            int i = idx % M;
            res[t].push_back({i, j});
        }
        start += count;
    }
    return res;
}

// (K) Every k-th element in row-major order
vector<vector<Task>> split_every_k(int M, int N, int num_threads)
{
    vector<vector<Task>> res(num_threads);
    for (auto& v : res) v.reserve((M * N + num_threads - 1) / num_threads);

    const int total = M * N;
    for (int idx = 0; idx < total; ++idx) {
        int t = idx % num_threads;
        int i = idx / N;
        int j = idx % N;
        res[t].push_back({i, j});
    }
    return res;
}