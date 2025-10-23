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

enum class Strategy { Rows, Cols, EveryK };

vector<vector<Task>> make_tasks(int M, int N, int T, Strategy s)
{
    switch (s) {
        case Strategy::Rows:   return split_by_rows(M, N, T);
        case Strategy::Cols:   return split_by_cols(M, N, T);
        case Strategy::EveryK: return split_every_k(M, N, T);
    }
    return {};
}

// Single-thread baseline for correctness & timing
vector<double> multiply_baseline(const vector<double>& A,
                                 const vector<double>& B,
                                 int M, int K, int N)
{
    vector<double> C(M * N, 0.0);
    for (int i = 0; i < M; ++i) {
        for (int j = 0; j < N; ++j) {
            double s = 0.0;
            for (int kk = 0; kk < K; ++kk) {
                s += getA(A, i, kk, K) * getB(B, kk, j, N);
            }
            getC(C, i, j, N) = s;
        }
    }
    return C;
}

// Compare two C matrices and report max absolute difference 
double max_abs_diff(const vector<double>& X, const vector<double>& Y)
{
    double m = 0.0;
    for (size_t i = 0; i < X.size(); ++i) {
        m = max(m, abs(X[i] - Y[i]));
    }
    return m;
}

int main(int argc, char** argv)
{
    // Usage: prog M K N T strategy[rows|cols|everyk] [--debug] [--random]
    if (argc < 6) {
        cerr << "Usage: " << argv[0] << " M K N T strategy(rows|cols|everyk) [--debug] [--random]\n";
        return 1;
    }

    const int M = stoi(argv[1]);
    const int K = stoi(argv[2]);
    const int N = stoi(argv[3]);
    const int T = max(1, stoi(argv[4]));
    string sarg = argv[5];

    Strategy strat = Strategy::Rows;
    if (sarg == "cols")    strat = Strategy::Cols;
    else if (sarg == "everyk") strat = Strategy::EveryK;
    else if (sarg != "rows") {
        cerr << "Unknown strategy: " << sarg << " (use rows|cols|everyk)\n";
        return 1;
    }

    bool use_random = false;
    for (int i = 6; i < argc; ++i) {
        string flag = argv[i];
        if (flag == "--debug") g_debug = true;
        else if (flag == "--random") use_random = true;
        else {
            cerr << "Unknown flag: " << flag << "\n";
            return 1;
        }
    }

    // Allocate A, B, C
    vector<double> A(M * K), B(K * N), C(M * N, 0.0);

    // Initialize inputs:
    // - Default: simple deterministic iota (1,2,3,...) to keep results stable
    // - Optional: --random to explore cache/branching less deterministically
    if (use_random) {
        mt19937_64 rng(42);
        uniform_real_distribution<double> dist(-1.0, 1.0);
        for (auto& v : A) v = dist(rng);
        for (auto& v : B) v = dist(rng);
    } else {
        iota(A.begin(), A.end(), 1.0);
        iota(B.begin(), B.end(), 1.0);
    }

    // Prepare tasks for chosen strategy
    auto tasks_per_thread = make_tasks(M, N, T, strat);

    // Time the threaded multiplication (spawn + compute + join)
    auto t0 = chrono::high_resolution_clock::now();

    vector<thread> threads;
    threads.reserve(T);
    for (int t = 0; t < T; ++t) {
        threads.emplace_back(worker, t, cref(tasks_per_thread[t]),
                             cref(A), cref(B), ref(C), M, K, N);
    }
    for (auto& th : threads) th.join();

    auto t1 = chrono::high_resolution_clock::now();
    double threaded_ms = chrono::duration<double, milli>(t1 - t0).count();

    cout << "Threaded (" << sarg << ", T=" << T << "): " << threaded_ms << " ms\n";

    // Baseline single-thread timing + correctness check
    auto b0 = chrono::high_resolution_clock::now();
    vector<double> C_ref = multiply_baseline(A, B, M, K, N);
    auto b1 = chrono::high_resolution_clock::now();
    double baseline_ms = chrono::duration<double, milli>(b1 - b0).count();

    double diff = max_abs_diff(C, C_ref);
    cout << "Baseline (1 thread): " << baseline_ms << " ms\n";
    cout << "Max |C - C_ref|: " << diff << "\n";

    // Tiny sanity print for very small matrices (kept compact)
    if (g_debug && M <= 9 && N <= 9) {
        cout << "C (threaded) first few rows:\n";
        for (int i = 0; i < M; ++i) {
            for (int j = 0; j < N; ++j) {
                cout << getC(C, i, j, N) << (j + 1 == N ? '\n' : ' ');
            }
        }
    }
    return 0;
}

/*
build command:
g++ -O2 -std=c++17 -pthread matrix_threads.cpp -o mtmul.exe


examples to run:

small debug test:
./mtmul.exe 9 9 9 4 rows --debug 

decently sized tests:
./mtmul.exe 512 512 512 4 rows
./mtmul.exe 1024 1024 1024 8 cols
./mtmul.exe 1024 1024 1024 8 everyk

time performance:

test 1:
4t - 44.2125 ms
1t - 0, too little

test 2:
4t - 57.669 ms
1t - 160.35 ms

test 3:
4t - 403.737 ms
1t - 1480.83 ms

test 4:
4t - 433.32 ms
1t - 1473.41 ms

*/